package web

import (
	"bufio"
	"context"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"embed"
	"encoding/csv"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"levelgateway/src/internal/buildinfo"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/storage"
	"levelgateway/src/internal/update"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
	"sync"
	"time"
)

//go:embed static/*
var assets embed.FS

type Credential struct {
	Salt       string `json:"salt"`
	Hash       string `json:"hash"`
	Iterations int    `json:"iterations"`
}

func InitAdmin(path, password string) error {
	if len(password) < 12 || len(password) > 128 {
		return errors.New("管理员密码长度须12～128字符")
	}
	if _, e := os.Stat(path); e == nil {
		return errors.New("凭据已存在，不覆盖已有管理员")
	}
	salt := make([]byte, 16)
	if _, e := rand.Read(salt); e != nil {
		return e
	}
	key, e := pbkdf2.Key(sha256.New, password, salt, 200000, 32)
	if e != nil {
		return e
	}
	c := Credential{hex.EncodeToString(salt), hex.EncodeToString(key), 200000}
	b, _ := json.Marshal(c)
	return config.WriteAtomic(path, b)
}

type session struct {
	CSRF    string
	Expires time.Time
}
type limiter struct {
	Attempts int
	Reset    time.Time
}
type Server struct {
	bridge          *licenseBridge
	upgrade         *update.Service
	authMu          sync.RWMutex
	authFile        string
	writeCredential func(string, []byte) error
	client          *http.Client
	base            string
	tokenFile       string
	credential      Credential
	mu              sync.Mutex
	sessions        map[string]session
	limits          map[string]limiter
	export          chan struct{}
}

func New(ipc, tokenFile, authFile string) (*Server, error) {
	b, e := os.ReadFile(authFile)
	if e != nil {
		return nil, fmt.Errorf("请先初始化管理员: %w", e)
	}
	var cred Credential
	if e = json.Unmarshal(b, &cred); e != nil {
		return nil, e
	}
	if cred.Iterations < 100000 || cred.Iterations > 1000000 {
		return nil, errors.New("凭据迭代参数无效")
	}
	tr := &http.Transport{MaxIdleConnsPerHost: 4}
	base := "http://" + ipc
	if strings.HasPrefix(ipc, "unix:") {
		path := strings.TrimPrefix(ipc, "unix:")
		tr.DialContext = func(ctx context.Context, _, _ string) (net.Conn, error) {
			return (&net.Dialer{}).DialContext(ctx, "unix", path)
		}
		base = "http://gateway"
	} else if !strings.HasPrefix(ipc, "127.0.0.1:") && !strings.HasPrefix(ipc, "[::1]:") {
		return nil, errors.New("内部地址须为本机")
	}
	return &Server{authFile: authFile, writeCredential: config.WriteAtomic, client: &http.Client{Transport: tr, Timeout: 10 * time.Second}, base: base, tokenFile: tokenFile, credential: cred, sessions: map[string]session{}, limits: map[string]limiter{}, export: make(chan struct{}, 1)}, nil
}
func randomToken() string {
	b := make([]byte, 32)
	if _, e := rand.Read(b); e != nil {
		panic(e)
	}
	return hex.EncodeToString(b)
}
func respond(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(v)
}
func sameOrigin(r *http.Request) bool {
	o := r.Header.Get("Origin")
	if o == "" {
		return true
	}
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	return o == scheme+"://"+r.Host
}

// Drain only a small known body on authentication rejection. This makes the
// HTTP error deterministic on clients whose TCP stack otherwise receives RST.
// Large uploads are rejected without draining; the short deadline bounds a slow sender.
func rejectRequest(w http.ResponseWriter, r *http.Request, message string, code int) {
	if r.Body != nil && r.ContentLength > 0 && r.ContentLength <= 65536 {
		controller := http.NewResponseController(w)
		_ = controller.SetReadDeadline(time.Now().Add(2 * time.Second))
		_, _ = io.CopyN(io.Discard, r.Body, r.ContentLength)
		_ = controller.SetReadDeadline(time.Time{})
	}
	http.Error(w, message, code)
}
func (s *Server) Handler() http.Handler {
	sub, _ := fs.Sub(assets, "static")
	static := http.FileServer(http.FS(sub))
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Gateway-Web-Version", buildinfo.WebVersion)
		if s.upgrade != nil {
			w.Header().Set("X-Gateway-Running-Release", update.RunningRelease(s.upgrade.Opts.Base))
		}
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'none'")
		if !strings.HasPrefix(r.URL.Path, "/api/") {
			w.Header().Set("Cache-Control", "no-cache")
			static.ServeHTTP(w, r)
			return
		}
		if r.URL.Path == "/api/admin/password" {
			s.authMu.Lock()
			defer s.authMu.Unlock()
		} else {
			s.authMu.RLock()
			defer s.authMu.RUnlock()
		}
		w.Header().Set("Cache-Control", "no-store")
		if r.Method != "GET" && !sameOrigin(r) {
			rejectRequest(w, r, "请求来源不匹配", 403)
			return
		}
		if r.URL.Path == "/api/login" {
			s.login(w, r)
			return
		}
		cookie, e := r.Cookie("gateway_session")
		if e != nil {
			rejectRequest(w, r, "请登录", 401)
			return
		}
		s.mu.Lock()
		sess, ok := s.sessions[cookie.Value]
		if !ok || time.Now().After(sess.Expires) {
			delete(s.sessions, cookie.Value)
			s.mu.Unlock()
			rejectRequest(w, r, "会话已过期", 401)
			return
		}
		sess.Expires = time.Now().Add(30 * time.Minute)
		s.sessions[cookie.Value] = sess
		s.mu.Unlock()
		if r.Method != "GET" && subtle.ConstantTimeCompare([]byte(r.Header.Get("X-CSRF-Token")), []byte(sess.CSRF)) != 1 {
			rejectRequest(w, r, "请求校验失败", 403)
			return
		}
		if r.URL.Path == "/api/session" && r.Method == "GET" {
			respond(w, map[string]string{"csrf": sess.CSRF})
			return
		}
		if r.URL.Path == "/api/logout" && r.Method == "POST" {
			s.mu.Lock()
			delete(s.sessions, cookie.Value)
			s.mu.Unlock()
			http.SetCookie(w, &http.Cookie{Name: "gateway_session", Value: "", Path: "/", MaxAge: -1, HttpOnly: true, Secure: r.TLS != nil, SameSite: http.SameSiteStrictMode})
			respond(w, map[string]bool{"ok": true})
			return
		}
		if strings.HasPrefix(r.URL.Path, "/api/upgrade/") {
			s.upgradeRequest(w, r)
			return
		}
		if r.URL.Path == "/api/history.csv" && r.Method == "GET" {
			s.csv(w, r)
			return
		}
		if r.URL.Path == "/api/admin/password" {
			s.changePassword(w, r)
			return
		}
		if r.URL.Path == "/api/maintenance/restart" {
			s.restartProgram(w, r)
			return
		}
		if strings.HasPrefix(r.URL.Path, "/api/license/") {
			s.licenseRequest(w, r)
			return
		}
		if strings.HasPrefix(r.URL.Path, "/api/network") {
			s.networkRequest(w, r)
			return
		}
		allowed := r.Method == "POST" && (r.URL.Path == "/api/time" || r.URL.Path == "/api/control/maintenance-reset" || r.URL.Path == "/api/control/level-adjust") || r.Method == "GET" && (r.URL.Path == "/api/status" || r.URL.Path == "/api/data" || r.URL.Path == "/api/config" || r.URL.Path == "/api/events" || r.URL.Path == "/api/history" || r.URL.Path == "/api/commands") || r.Method == "PUT" && (r.URL.Path == "/api/config" || r.URL.Path == "/api/simulator/fault")
		if !allowed {
			http.NotFound(w, r)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, 65536)
		resp, e := s.request(r.Context(), r.Method, r.URL.RequestURI(), r.Body)
		if e != nil {
			http.Error(w, "采集服务暂不可用: "+e.Error(), 503)
			return
		}
		defer resp.Body.Close()
		if s.bridge != nil && r.URL.Path == "/api/status" && resp.StatusCode == http.StatusOK {
			var status map[string]json.RawMessage
			if err := json.NewDecoder(io.LimitReader(resp.Body, 4*1024*1024)).Decode(&status); err != nil || status == nil {
				http.Error(w, "采集状态读取失败", 502)
				return
			}
			status["license"], _ = json.Marshal(s.bridge.status())
			respond(w, status)
			return
		}
		w.Header().Set("Content-Type", resp.Header.Get("Content-Type"))
		w.WriteHeader(resp.StatusCode)
		_, _ = io.Copy(w, io.LimitReader(resp.Body, 4*1024*1024))
	})
}
func (s *Server) login(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "方法不支持", 405)
		return
	}
	ip, _, _ := net.SplitHostPort(r.RemoteAddr)
	s.mu.Lock()
	now := time.Now()
	for k, v := range s.limits {
		if now.After(v.Reset) {
			delete(s.limits, k)
		}
	}
	l := s.limits[ip]
	if now.After(l.Reset) {
		l = limiter{0, now.Add(time.Minute)}
	}
	if l.Attempts >= 5 || len(s.limits) >= 128 {
		s.mu.Unlock()
		http.Error(w, "尝试次数过多，请稍后重试", 429)
		return
	}
	l.Attempts++
	s.limits[ip] = l
	s.mu.Unlock()
	r.Body = http.MaxBytesReader(w, r.Body, 2048)
	var body struct {
		Password string `json:"password"`
	}
	if json.NewDecoder(r.Body).Decode(&body) != nil || len(body.Password) > 128 {
		http.Error(w, "登录数据无效", 400)
		return
	}
	salt, e := hex.DecodeString(s.credential.Salt)
	if e != nil {
		http.Error(w, "凭据异常", 500)
		return
	}
	key, e := pbkdf2.Key(sha256.New, body.Password, salt, s.credential.Iterations, 32)
	if e != nil || subtle.ConstantTimeCompare([]byte(hex.EncodeToString(key)), []byte(s.credential.Hash)) != 1 {
		http.Error(w, "密码错误", 401)
		return
	}
	id, csrf := randomToken(), randomToken()
	s.mu.Lock()
	for k, v := range s.sessions {
		if now.After(v.Expires) {
			delete(s.sessions, k)
		}
	}
	if len(s.sessions) >= 64 {
		s.mu.Unlock()
		http.Error(w, "会话数量已达上限", 429)
		return
	}
	s.sessions[id] = session{csrf, now.Add(30 * time.Minute)}
	delete(s.limits, ip)
	s.mu.Unlock()
	http.SetCookie(w, &http.Cookie{Name: "gateway_session", Value: id, Path: "/", HttpOnly: true, Secure: r.TLS != nil, SameSite: http.SameSiteStrictMode})
	respond(w, map[string]string{"csrf": csrf})
}
func (s *Server) request(ctx context.Context, method, path string, body io.Reader) (*http.Response, error) {
	b, e := os.ReadFile(s.tokenFile)
	if e != nil {
		return nil, e
	}
	req, e := http.NewRequestWithContext(ctx, method, s.base+path, body)
	if e != nil {
		return nil, e
	}
	req.Header.Set("Authorization", "Bearer "+strings.TrimSpace(string(b)))
	if method != "GET" {
		req.Header.Set("Content-Type", "application/json")
	}
	return s.client.Do(req)
}
func (s *Server) csv(w http.ResponseWriter, r *http.Request) {
	select {
	case s.export <- struct{}{}:
		defer func() { <-s.export }()
	default:
		http.Error(w, "已有历史导出在运行", 429)
		return
	}
	q := r.URL.Query()
	filterSlot := 0
	if values, present := q["slot"]; present {
		var err error
		if len(values) != 1 {
			http.Error(w, "只能选择一个槽位", 400)
			return
		}
		filterSlot, err = strconv.Atoi(values[0])
		if err != nil || filterSlot < 0 || filterSlot > config.MaxMeters {
			http.Error(w, "槽位须为0（全部）或1～64", 400)
			return
		}
	}
	from, e := strconv.ParseInt(q.Get("from"), 10, 64)
	if e != nil {
		http.Error(w, "起始时间无效", 400)
		return
	}
	to, e := strconv.ParseInt(q.Get("to"), 10, 64)
	if e != nil || to < from || to-from > 7*86400000 {
		http.Error(w, "单次导出范围最多7天", 400)
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), 60*time.Second)
	defer cancel()
	// Freeze export-time labels. Do not attach today's name to a different binding.
	resp, e := s.request(ctx, "GET", "/api/config", nil)
	if e != nil {
		http.Error(w, "无法读取仪表名称", 503)
		return
	}
	var cfg config.Config
	e = json.NewDecoder(io.LimitReader(resp.Body, 65536)).Decode(&cfg)
	resp.Body.Close()
	if e != nil || resp.StatusCode != 200 {
		http.Error(w, "无法读取仪表名称", 503)
		return
	}
	meters := make(map[int]config.Meter)
	for _, meter := range cfg.Meters {
		meters[meter.Slot] = meter
	}
	after := int64(0)
	lastSlot := 0
	first := true
	writer := csv.NewWriter(w)
	for {
		path := fmt.Sprintf("/api/history?from=%d&to=%d&after=%d&after_slot=%d&slot=%d", from, to, after, lastSlot, filterSlot)
		resp, e := s.request(ctx, "GET", path, nil)
		if e != nil {
			if first {
				http.Error(w, e.Error(), 503)
			}
			return
		}
		var rows []storage.Record
		e = json.NewDecoder(io.LimitReader(resp.Body, 2*1024*1024)).Decode(&rows)
		resp.Body.Close()
		if e != nil || resp.StatusCode != 200 {
			if first {
				http.Error(w, "历史查询失败", 503)
			}
			return
		}
		if first {
			w.Header().Set("Content-Type", "text/csv; charset=utf-8")
			filename := "level-history.csv"
			if filterSlot != 0 {
				filename = fmt.Sprintf("level-history-slot-%02d.csv", filterSlot)
			}
			w.Header().Set("Content-Disposition", "attachment; filename="+filename)
			_, _ = io.WriteString(w, "\ufeff")
			_ = writer.Write([]string{"记录UTC时间", "槽位", "绑定版本", "采集UTC时间", "液位mm", "平均温度℃", "当前密度kg/m3", "平均密度kg/m3", "浮子状态", "故障位", "质量", "有效字段", "液位采集UTC毫秒", "温度采集UTC毫秒", "密度采集UTC毫秒", "液位计名称", "名称说明"})
			first = false
		}
		for _, v := range rows {
			name, note := "", "槽位当前未配置"
			if meter, ok := meters[v.Slot]; ok {
				note = "历史绑定，与当前仪表不同"
				if meter.Binding == v.Binding {
					name, note = csvName(meter.Name), "导出时配置名称，非历史名称"
				}
			}
			_ = writer.Write([]string{time.UnixMilli(v.RecordMS).UTC().Format(time.RFC3339Nano), strconv.Itoa(v.Slot), fmt.Sprint(v.Binding), fmt.Sprint(v.ReadMS), fmt.Sprintf("%.1f", float64(v.Level)/10), fmt.Sprintf("%.2f", float64(v.Temperature)/100), fmt.Sprintf("%.1f", float64(v.Density)/10), fmt.Sprintf("%.1f", float64(v.AverageDensity)/10), fmt.Sprint(v.State), fmt.Sprint(v.Fault), fmt.Sprint(v.Quality), fmt.Sprint(v.Valid), fmt.Sprint(v.LevelMS), fmt.Sprint(v.TemperatureMS), fmt.Sprint(v.DensityMS), name, note})
			after = v.RecordMS
			lastSlot = v.Slot
		}
		writer.Flush()
		if writer.Error() != nil {
			return
		}
		if f, ok := w.(http.Flusher); ok {
			f.Flush()
		}
		if len(rows) < 2000 {
			return
		}
	}
}

// A configured name is text even if it starts with an Excel formula marker.
func csvName(name string) string {
	trimmed := strings.TrimLeft(name, " \t\r\n")
	if strings.HasPrefix(name, "\t") || strings.HasPrefix(name, "\r") || strings.HasPrefix(name, "\n") || len(trimmed) > 0 && strings.ContainsRune("=+-@", rune(trimmed[0])) {
		return "'" + name
	}
	return name
}
func ReadPassword() (string, error) {
	line, e := bufio.NewReader(os.Stdin).ReadString('\n')
	if e != nil && e != io.EOF {
		return "", e
	}
	return strings.TrimSpace(line), nil
}

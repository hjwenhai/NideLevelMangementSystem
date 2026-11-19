package web

import (
	"bytes"
	"crypto/pbkdf2"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"log"
	"net"
	"net/http"
	"strings"
	"time"
)

func credentialMatches(c Credential, password string) bool {
	salt, err := hex.DecodeString(c.Salt)
	if err != nil {
		return false
	}
	key, err := pbkdf2.Key(sha256.New, password, salt, c.Iterations, 32)
	return err == nil && subtle.ConstantTimeCompare([]byte(hex.EncodeToString(key)), []byte(c.Hash)) == 1
}
func requestSource(r *http.Request) string {
	ip, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		ip = "unknown"
	}
	return "web:" + ip
}
func decodeMaintenance(w http.ResponseWriter, r *http.Request, body any) error {
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	decoder := json.NewDecoder(r.Body)
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(body); err != nil {
		return err
	}
	if decoder.Decode(&struct{}{}) != io.EOF {
		return fmt.Errorf("只允许一个JSON对象")
	}
	return nil
}
func (s *Server) passwordAudit(r *http.Request, kind, id string) error {
	body, _ := json.Marshal(map[string]string{"kind": kind, "id": id, "source": requestSource(r)})
	response, err := s.request(r.Context(), "POST", "/api/maintenance/password-audit", bytes.NewReader(body))
	if err != nil {
		return err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return fmt.Errorf("维护审计暂不可用或正在重启")
	}
	return nil
}
func (s *Server) changePassword(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "方法不支持", 405)
		return
	}
	var body struct {
		Current string `json:"current_password"`
		New     string `json:"new_password"`
		Confirm string `json:"confirm_password"`
	}
	if decodeMaintenance(w, r, &body) != nil {
		http.Error(w, "密码表单无效", 400)
		return
	}
	if len(body.Current) > 128 || len(body.New) < 12 || len(body.New) > 128 || body.New != body.Confirm || strings.TrimSpace(body.New) != body.New {
		http.Error(w, "新密码须12～128个ASCII字符，两次一致且无首尾空格", 400)
		return
	}
	for _, v := range body.New {
		if v < 32 || v > 126 {
			http.Error(w, "新密码请使用ASCII字符", 400)
			return
		}
	}
	limitKey := "password:" + requestSource(r)
	s.mu.Lock()
	now := time.Now()
	entry := s.limits[limitKey]
	for k, v := range s.limits {
		if now.After(v.Reset) {
			delete(s.limits, k)
		}
	}
	if now.After(entry.Reset) {
		entry = limiter{0, now.Add(time.Minute)}
	}
	if entry.Attempts >= 5 || len(s.limits) >= 128 {
		s.mu.Unlock()
		http.Error(w, "尝试次数过多，请一分钟后重试", 429)
		return
	}
	entry.Attempts++
	s.limits[limitKey] = entry
	s.mu.Unlock()
	if !credentialMatches(s.credential, body.Current) {
		http.Error(w, "原密码错误", 400)
		return
	}
	if body.New == body.Current {
		http.Error(w, "新密码不能与原密码相同", 400)
		return
	}
	salt := make([]byte, 16)
	if _, err := rand.Read(salt); err != nil {
		http.Error(w, "生成新凭据失败", 500)
		return
	}
	key, err := pbkdf2.Key(sha256.New, body.New, salt, 200000, 32)
	if err != nil {
		http.Error(w, "生成新凭据失败", 500)
		return
	}
	next := Credential{Salt: hex.EncodeToString(salt), Hash: hex.EncodeToString(key), Iterations: 200000}
	data, _ := json.Marshal(next)
	id := randomToken()[:32]
	if err = s.passwordAudit(r, "admin_password_change_prepared", id); err != nil {
		http.Error(w, "无法保存维护记录或正在重启，原密码未修改", 503)
		return
	}
	err = s.writeCredential(s.authFile, data)
	var committed *config.CommitError
	if err != nil && !errors.As(err, &committed) {
		_ = s.passwordAudit(r, "admin_password_change_failed", id)
		http.Error(w, "保存凭据失败，原密码未修改", 500)
		return
	}
	// Even on a directory-sync warning, rename committed the new on-disk identity.
	s.credential = next
	s.mu.Lock()
	s.sessions = map[string]session{}
	delete(s.limits, limitKey)
	s.mu.Unlock()
	warning := ""
	if committed != nil {
		warning = "新密码已生效，但目录同步失败，请检查存储并保留本次维护记录"
	}
	if e := s.passwordAudit(r, "admin_password_changed", id); e != nil {
		warning += " 新密码已生效，完成审计登记失败，请核对采集服务和近期事件"
		log.Print("管理员密码已修改，完成审计登记失败")
	}
	http.SetCookie(w, &http.Cookie{Name: "gateway_session", Value: "", Path: "/", MaxAge: -1, HttpOnly: true, Secure: r.TLS != nil, SameSite: http.SameSiteStrictMode})
	respond(w, map[string]any{"ok": true, "warning": strings.TrimSpace(warning)})
}
func (s *Server) restartProgram(w http.ResponseWriter, r *http.Request) {
	if r.Method != "POST" {
		http.Error(w, "方法不支持", 405)
		return
	}
	var body struct {
		Confirmed bool `json:"confirmed"`
	}
	if decodeMaintenance(w, r, &body) != nil || !body.Confirmed {
		http.Error(w, "请先确认通讯中断且重启不等于停止浮子", 400)
		return
	}
	data, _ := json.Marshal(map[string]any{"confirmed": true, "source": requestSource(r)})
	response, err := s.request(r.Context(), "POST", "/api/maintenance/restart", bytes.NewReader(data))
	if err != nil {
		http.Error(w, "采集服务不可用，无法确认重启是否受理，请检查诊断后再操作", 503)
		return
	}
	defer response.Body.Close()
	w.Header().Set("Content-Type", response.Header.Get("Content-Type"))
	w.WriteHeader(response.StatusCode)
	_, _ = io.Copy(w, io.LimitReader(response.Body, 4096))
}

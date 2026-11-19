package network

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/config"
	"net"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

type Options struct {
	Base    string
	Fixture bool
	Root    string
}
type snapshot struct {
	Path string `json:"path"`
	Data []byte `json:"data"`
}
type State struct {
	ID            string     `json:"id"`
	Phase         string     `json:"phase"`
	Message       string     `json:"message"`
	Settings      Settings   `json:"settings"`
	Before        Settings   `json:"before"`
	Files         []snapshot `json:"files"`
	Source        string     `json:"source"`
	ConfirmSource string     `json:"confirm_source"`
	DeadlineMS    int64      `json:"deadline_ms"`
	PID           int        `json:"pid"`
	Boot          string     `json:"boot"`
}
type Service struct{ Opts Options }

func New(o Options) (*Service, error) {
	if !filepath.IsAbs(o.Base) {
		return nil, fmt.Errorf("网络管理目录须为绝对路径")
	}
	if o.Fixture && runtime.GOOS != "windows" {
		return nil, fmt.Errorf("网络测试仅限Windows模拟环境")
	}
	if o.Root == "" {
		o.Root = string(filepath.Separator)
	}
	return &Service{Opts: o}, nil
}
func (s *Service) path(p ...string) string {
	return filepath.Join(append([]string{s.Opts.Base}, p...)...)
}
func (s *Service) vendor(name string) string {
	if s.Opts.Fixture {
		return s.path("fixture", name)
	}
	return filepath.Join(s.Opts.Root, "etc", name)
}
func (s *Service) paths() []string {
	return []string{s.vendor("net.conf"), s.vendor("net2.conf"), s.path("config", "gateway.json"), s.path("config", "service.env")}
}
func regular(path string) ([]byte, error) {
	info, e := os.Lstat(path)
	if e != nil {
		return nil, e
	}
	if !info.Mode().IsRegular() || info.Size() > 131072 {
		return nil, fmt.Errorf("文件不是普通文件或过大: %s", path)
	}
	return os.ReadFile(path)
}
func (s *Service) Available() error {
	if !s.Opts.Fixture && runtime.GOOS != "linux" {
		return fmt.Errorf("仅支持厂家Buildroot静态双网口系统")
	}
	for _, p := range s.paths() {
		if _, e := regular(p); e != nil {
			return fmt.Errorf("网络配置文件不可用: %s", p)
		}
	}
	for _, name := range []string{"net.conf", "net2.conf"} {
		b, _ := regular(s.vendor(name))
		if _, e := ParseVendor(name, b); e != nil {
			return e
		}
	}
	return nil
}
func (s *Service) read() (State, error) {
	b, e := regular(s.path("data", "network-state.json"))
	if os.IsNotExist(e) {
		return State{}, nil
	}
	if e != nil {
		return State{}, e
	}
	var st State
	e = json.Unmarshal(b, &st)
	if e == nil && (!idValid(st.ID) || !knownPhase(st.Phase)) {
		e = fmt.Errorf("网络事务记录损坏，请通过Console恢复备份")
	}
	return st, e
}
func idValid(id string) bool { b, e := hex.DecodeString(id); return e == nil && len(b) == 16 }
func knownPhase(p string) bool {
	switch p {
	case "queued", "applying", "trial", "confirming", "confirmed", "rolling_back", "rolled_back", "failed":
		return true
	}
	return false
}
func pending(p string) bool { return p != "" && p != "confirmed" && p != "rolled_back" }
func (s *Service) save(st State) error {
	b, _ := json.MarshalIndent(st, "", "  ")
	return config.WriteAtomic(s.path("data", "network-state.json"), b)
}
func (s *Service) locked(fn func() error) error {
	if e := os.MkdirAll(s.path("data"), 0700); e != nil {
		return e
	}
	unlock, e := lockFile(s.path("data", "network-state.lock"))
	if e != nil {
		return e
	}
	defer unlock()
	return fn()
}
func (s *Service) Busy() bool {
	if s == nil {
		return false
	}
	st, e := s.read()
	return e != nil || pending(st.Phase)
}
func (s *Service) settings() (Settings, error) {
	var c Settings
	for i, n := range []string{"net.conf", "net2.conf"} {
		b, e := regular(s.vendor(n))
		if e != nil {
			return c, e
		}
		p, e := ParseVendor(fmt.Sprintf("eth%d", i), b)
		if e != nil {
			return c, e
		}
		c.Interfaces = append(c.Interfaces, p)
	}
	cfg, e := config.Load(s.path("config", "gateway.json"))
	if e != nil {
		return c, e
	}
	host, _, e := net.SplitHostPort(cfg.TCP.Listen)
	if e != nil {
		return c, e
	}
	c.TCPInterface = "both"
	for _, p := range c.Interfaces {
		if p.IP == host {
			c.TCPInterface = p.Name
		}
	}
	if host != "0.0.0.0" && c.TCPInterface == "both" {
		return c, fmt.Errorf("TCP监听地址与两个网口不一致，请先在端口配置中核对")
	}
	return c, nil
}
func (s *Service) Status() Status {
	out := Status{Phase: "idle", Message: "修改后需在新地址确认，180秒未确认自动恢复"}
	if s == nil {
		out.Message = "当前启动方式未启用系统网络设置"
		return out
	}
	if e := s.Available(); e != nil {
		out.Message = e.Error()
		return out
	}
	out.Supported = true
	c, e := s.settings()
	out.Settings = c
	if e != nil {
		out.Warning = e.Error()
	}
	if b, e := regular(s.path("config", "service.env")); e == nil {
		_, out.WebPort, _ = net.SplitHostPort(envValue(b, "WEB_LISTEN"))
	}
	st, e := s.read()
	if e != nil {
		out.Busy = true
		out.Phase = "failed"
		out.Message = e.Error()
		return out
	}
	if st.ID != "" {
		out.ID = st.ID
		out.Phase = st.Phase
		out.Busy = pending(st.Phase)
		out.Message = st.Message
		out.DeadlineMS = st.DeadlineMS
		if out.Busy {
			out.Settings = st.Settings
		}
	}
	out.Actual, _ = s.actual()
	if !s.Opts.Fixture {
		b, _ := regular(filepath.Join(s.Opts.Root, "etc", "htset", "bootset"))
		r, _ := regular(s.vendor("reset.conf"))
		for _, line := range strings.Split(string(b), "\n") {
			line = strings.TrimSpace(line)
			if !strings.HasPrefix(line, "#") && strings.Contains(line, "sysrst") {
				out.FUNAvailable = len(r) > 0
			}
		}
	}
	out.Warning = strings.TrimSpace(out.Warning + " " + "FUN功能取决于厂家sysrst服务；多网关恢复前请直连单台，避免默认IP冲突。")
	return out
}
func (s *Service) Begin(c Settings, source string) (Status, error) {
	if e := c.Validate(); e != nil {
		return s.Status(), e
	}
	if e := s.Available(); e != nil {
		return s.Status(), e
	}
	if e := s.preflight(); e != nil {
		return s.Status(), e
	}
	e := s.locked(func() error {
		old, e := s.read()
		if e != nil {
			return e
		}
		if pending(old.Phase) {
			return fmt.Errorf("已有网络事务未结束")
		}

		// Use the same upload/install reservations as the application updater.
		// Publish the durable network gate before releasing either reservation.
		uploadRoot := s.path("data", "upgrades")
		if e := os.MkdirAll(uploadRoot, 0700); e != nil {
			return e
		}
		uploadLock := filepath.Join(uploadRoot, "upload.lock")
		if e := os.Mkdir(uploadLock, 0700); e != nil {
			return fmt.Errorf("正在上传或准备升级，暂不能修改网络")
		}
		defer os.Remove(uploadLock)
		installLock := s.path("data", "install.lock")
		if e := os.Mkdir(installLock, 0700); e != nil {
			return fmt.Errorf("安装或升级正在进行")
		}
		defer os.Remove(installLock)

		before, e := s.settings()
		if e != nil {
			return e
		}
		if e = before.Validate(); e != nil {
			return fmt.Errorf("现有网络参数需要厂家先整理: %w", e)
		}
		// Capture all files before any changes; the durable journal is sufficient to
		// restore a partially committed pair of system network files after power loss.
		files := []snapshot{}
		for _, p := range s.paths() {
			b, e := regular(p)
			if e != nil {
				return e
			}
			files = append(files, snapshot{p, b})
		}
		total := 0
		for _, f := range files {
			total += len(f.Data)
		}
		if total > 65536 {
			return fmt.Errorf("现有网络及服务配置过大，请厂家先核查；未建立网络事务")
		}
		id := make([]byte, 16)
		if _, e = rand.Read(id); e != nil {
			return e
		}
		st := State{ID: hex.EncodeToString(id), Phase: "queued", Settings: c, Before: before, Files: files, Source: source, Message: "已受理，正在安排网络试用；请勿重复提交"}
		backup := s.path("data", "network-backups", st.ID)
		if e = os.MkdirAll(backup, 0700); e != nil {
			return e
		}
		for i, f := range files {
			if e = config.WriteAtomic(filepath.Join(backup, fmt.Sprintf("%d-%s", i, filepath.Base(f.Path))), f.Data); e != nil {
				return e
			}
		}
		return s.save(st)
	})
	return s.Status(), e
}
func (s *Service) Confirm(id, localIP, source string) (Status, error) {
	e := s.locked(func() error {
		st, e := s.read()
		if e != nil {
			return e
		}
		if st.ID != id || st.Phase != "trial" {
			return fmt.Errorf("网络试用已结束或任务不匹配")
		}
		valid := false
		for _, p := range st.Settings.Interfaces {
			valid = valid || p.IP == localIP
		}
		if s.Opts.Fixture && net.ParseIP(localIP).IsLoopback() {
			valid = true
		}
		if !valid {
			return fmt.Errorf("请通过本次设置的新IP地址登录后确认")
		}
		// Worker uses a monotonic timer as the authoritative deadline; its lock
		// serializes this confirmation against timeout rollback.
		st.Phase = "confirming"
		st.ConfirmSource = source
		st.Message = "连接确认已受理，正在保存"
		return s.save(st)
	})
	return s.Status(), e
}
func (s *Service) AbortUnlaunched(id string) error {
	return s.locked(func() error {
		st, e := s.read()
		if e != nil {
			return e
		}
		if st.ID != id || st.Phase != "queued" {
			return fmt.Errorf("网络任务不匹配")
		}
		st.Phase = "rolled_back"
		st.Message = "启动网络执行器失败，配置未修改"
		return s.save(st)
	})
}
func envValue(b []byte, key string) string {
	v := ""
	for _, line := range strings.Split(string(b), "\n") {
		line = strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(line), "export "))
		if strings.HasPrefix(line, key+"=") {
			v = strings.Trim(strings.TrimSpace(strings.SplitN(line, "=", 2)[1]), `"'`)
		}
	}
	return v
}
func setWeb(b []byte) ([]byte, error) {
	v := envValue(b, "WEB_LISTEN")
	if v == "" {
		v = "127.0.0.1:8088"
	}
	next, e := Wildcard(v)
	if e != nil {
		return nil, e
	}
	lines := []string{}
	for _, l := range strings.Split(strings.TrimRight(string(b), "\r\n"), "\n") {
		line := strings.TrimSpace(strings.TrimPrefix(strings.TrimSpace(l), "export "))
		if !strings.HasPrefix(line, "WEB_LISTEN=") {
			lines = append(lines, l)
		}
	}
	return []byte(strings.Join(append(lines, "WEB_LISTEN="+next), "\n") + "\n"), nil
}

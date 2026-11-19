package update

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/network"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

type Options struct {
	Base          string `json:"base"`
	IPC           string `json:"ipc"`
	WebURL        string `json:"web_url"`
	TLSCert       string `json:"tls_cert"`
	Shell         string `json:"shell"`
	ServiceScript string `json:"service_script"`
	Fixture       bool   `json:"fixture"`
}
type State struct {
	ID        string   `json:"id"`
	Phase     string   `json:"phase"`
	Manifest  Manifest `json:"manifest"`
	Previous  string   `json:"previous"`
	Source    string   `json:"source"`
	Message   string   `json:"message"`
	UpdatedMS int64    `json:"updated_ms"`
	BootID    string   `json:"boot_id"`
	PID       int      `json:"pid"`
}
type Status struct {
	Supported   bool   `json:"supported"`
	ID          string `json:"id"`
	Phase       string `json:"phase"`
	Current     string `json:"current"`
	Target      string `json:"target"`
	Software    string `json:"software"`
	Arch        string `json:"arch"`
	Notes       string `json:"notes"`
	Message     string `json:"message"`
	UpdatedMS   int64  `json:"updated_ms"`
	Busy        bool   `json:"busy"`
	UploadLimit int64  `json:"upload_limit"`
}
type Service struct{ Opts Options }

func New(opts Options) (*Service, error) {
	if !filepath.IsAbs(opts.Base) {
		return nil, fmt.Errorf("升级根目录须为绝对路径")
	}
	base, e := filepath.EvalSymlinks(opts.Base)
	if e != nil {
		return nil, e
	}
	opts.Base = base
	if opts.Fixture && runtime.GOOS != "windows" {
		return nil, fmt.Errorf("测试启动选项仅限Windows本机模拟")
	}
	return &Service{Opts: opts}, nil
}
func (s *Service) path(parts ...string) string {
	return filepath.Join(append([]string{s.Opts.Base}, parts...)...)
}
func (s *Service) arch() string {
	if s.Opts.Fixture {
		return "windows-amd64-test"
	}
	if runtime.GOOS == "linux" && runtime.GOARCH == "arm64" {
		return "linux-arm64"
	}
	if runtime.GOOS == "linux" && runtime.GOARCH == "arm" {
		return "linux-armv7"
	}
	return "unsupported"
}
func (s *Service) job(id string) (string, error) {
	if !idPattern.MatchString(id) {
		return "", fmt.Errorf("升级任务编号无效")
	}
	p := s.path("data", "upgrades", "jobs", id)
	rel, e := filepath.Rel(s.Opts.Base, p)
	if e != nil || strings.HasPrefix(rel, "..") || filepath.IsAbs(rel) {
		return "", fmt.Errorf("升级目录越界")
	}
	return p, nil
}
func (s *Service) read() (State, error) {
	b, e := os.ReadFile(s.path("data", "upgrades", "state.json"))
	if e != nil {
		return State{}, e
	}
	if len(b) > 65536 {
		return State{}, fmt.Errorf("升级状态文件过大")
	}
	var st State
	e = strictJSON(b, &st)
	if e != nil || !idPattern.MatchString(st.ID) || !knownPhase(st.Phase) {
		return State{}, fmt.Errorf("升级状态损坏，须厂家核查")
	}
	return st, nil
}
func knownPhase(p string) bool {
	switch p {
	case "ready", "queued", "preparing", "backing_up", "switching", "starting", "verifying", "rolling_back", "succeeded", "rolled_back", "failed", "needs_attention":
		return true
	}
	return false
}
func active(p string) bool {
	return p != "" && p != "ready" && p != "succeeded" && p != "rolled_back" && p != "failed"
}
func (s *Service) save(st State) error {
	st.UpdatedMS = time.Now().UnixMilli()
	b, e := json.Marshal(st)
	if e != nil {
		return e
	}
	if e = config.WriteAtomic(s.path("data", "upgrades", "jobs", st.ID, "state.json"), b); e != nil {
		return e
	}
	return config.WriteAtomic(s.path("data", "upgrades", "state.json"), b)
}
func (s *Service) phase(st *State, p, msg string) error {
	st.Phase = p
	st.Message = msg
	return s.save(*st)
}
func (s *Service) BlocksWrites() bool {
	if s == nil {
		return false
	}
	if _, e := os.Lstat(s.path("data", "install.lock")); !os.IsNotExist(e) {
		return true
	}
	st, e := s.read()
	return e != nil && !os.IsNotExist(e) || active(st.Phase)
}
func (s *Service) Current() (string, error) {
	p, e := filepath.EvalSymlinks(s.path("current"))
	if e != nil {
		return "", e
	}
	parent, e := filepath.EvalSymlinks(s.path("releases"))
	if e != nil {
		return "", e
	}
	if filepath.Dir(p) != parent {
		return "", fmt.Errorf("当前版本目录不在releases下")
	}
	rel := filepath.Base(p)
	if _, e = releaseParts(rel); e != nil {
		return "", e
	}
	b, e := os.ReadFile(filepath.Join(p, "RELEASE"))
	if e != nil || strings.TrimSpace(string(b)) != rel {
		return "", fmt.Errorf("当前版本标记不一致")
	}
	return rel, nil
}
func RunningRelease(base string) string {
	exe, e := os.Executable()
	if e != nil {
		return ""
	}
	exe, e = filepath.EvalSymlinks(exe)
	if e != nil {
		return ""
	}
	r := filepath.Dir(filepath.Dir(exe))
	parent, e := filepath.EvalSymlinks(filepath.Join(base, "releases"))
	if e != nil || filepath.Dir(r) != parent {
		return ""
	}
	return filepath.Base(r)
}
func (s *Service) supported() error {
	if s.arch() == "unsupported" {
		return fmt.Errorf("当前平台未启用网页升级")
	}
	current, e := s.Current()
	if e != nil {
		return e
	}
	root := s.path("releases", current)
	for name, value := range map[string]string{"ARCH": s.arch(), "NORTH_VERSION": "3.2", "UPDATE_VERSION": "1"} {
		b, e := os.ReadFile(filepath.Join(root, name))
		if e != nil || strings.TrimSpace(string(b)) != value {
			return fmt.Errorf("当前版本缺少兼容升级基础: %s", name)
		}
	}
	if _, e = loadTrust(s.path("config", "update-trust.json")); e != nil {
		return e
	}
	if _, e = os.Stat(filepath.Join(root, "bin", "gateway-updater")); e != nil {
		return e
	}
	return nil
}
func (s *Service) Status() Status {
	if s == nil {
		return Status{Message: "当前启动方式未启用网页升级", UploadLimit: UploadLimit}
	}
	out := Status{UploadLimit: UploadLimit, Arch: s.arch(), Busy: s.BlocksWrites()}
	current, e := s.Current()
	out.Current = current
	if e = s.supported(); e != nil {
		out.Message = e.Error()
		return out
	}
	out.Supported = true
	st, e := s.read()
	if os.IsNotExist(e) {
		out.Message = "请选择厂家提供的签名升级包"
		return out
	}
	if e != nil {
		out.Phase = "needs_attention"
		out.Busy = true
		out.Message = e.Error()
		return out
	}
	out.ID = st.ID
	out.Phase = st.Phase
	out.Target = st.Manifest.Release
	out.Software = st.Manifest.Software
	out.Notes = st.Manifest.Notes
	out.Message = st.Message
	out.UpdatedMS = st.UpdatedMS
	if active(st.Phase) && st.Phase != "needs_attention" && time.Since(time.UnixMilli(st.UpdatedMS)) > 30*time.Second && (st.PID == 0 || !workerAlive(st.PID, st.ID)) {
		out.Phase = "needs_attention"
		out.Message = "升级执行器已退出且任务未确认完成，请厂家核查；保持控制互锁，不自动重试"
	}
	return out
}
func (s *Service) checkSpace(needed int64) error {
	free, e := diskFree(s.Opts.Base)
	if e != nil {
		return e
	}
	if free < uint64(needed) {
		return fmt.Errorf("设备剩余空间不足，至少需预留%d MiB", needed>>20)
	}
	return nil
}
func (s *Service) needed(expanded int64) int64 {
	n := expanded + UploadLimit + (64 << 20)
	for _, name := range []string{"gateway-v2.db", "gateway-v2.db-wal"} {
		if f, e := os.Stat(s.path("data", name)); e == nil {
			n += f.Size() * 2
		}
	}
	return n
}
func (s *Service) removeJob(id string) {
	p, e := s.job(id)
	if e != nil {
		return
	}
	parent, e := filepath.EvalSymlinks(filepath.Dir(p))
	if e != nil || parent != s.path("data", "upgrades", "jobs") {
		return
	}
	if i, e := os.Lstat(p); e == nil && i.IsDir() && i.Mode()&os.ModeSymlink == 0 {
		_ = os.RemoveAll(p)
	}
}
func (s *Service) Stage(ctx context.Context, r io.Reader) (Status, error) {
	return s.stage(ctx, r, "")
}

func (s *Service) StageBridgeTarget(ctx context.Context, r io.Reader) (Status, error) {
	return s.stage(ctx, r, bridgeTarget)
}

func (s *Service) stage(ctx context.Context, r io.Reader, target string) (Status, error) {
	if (&network.Service{Opts: network.Options{Base: s.Opts.Base}}).Busy() {
		return s.Status(), fmt.Errorf("网络试用未结束，不能上传升级包")
	}
	if e := s.supported(); e != nil {
		return s.Status(), e
	}
	if s.BlocksWrites() {
		return s.Status(), fmt.Errorf("已有升级任务或未处理的升级状态")
	}
	root := s.path("data", "upgrades")
	if e := os.MkdirAll(root, 0700); e != nil {
		return s.Status(), e
	}
	lock := filepath.Join(root, "upload.lock")
	if e := os.Mkdir(lock, 0700); e != nil {
		return s.Status(), fmt.Errorf("另一上传正在进行，或旧上传锁须核查")
	}
	defer os.Remove(lock)
	if (&network.Service{Opts: network.Options{Base: s.Opts.Base}}).Busy() {
		return s.Status(), fmt.Errorf("网络试用未结束，不能上传升级包")
	}
	if s.BlocksWrites() {
		return s.Status(), fmt.Errorf("升级正在进行")
	}
	if e := s.checkSpace(s.needed(ExpandLimit)); e != nil {
		return s.Status(), e
	}
	b := make([]byte, 16)
	if _, e := rand.Read(b); e != nil {
		return s.Status(), e
	}
	id := hex.EncodeToString(b)
	job, _ := s.job(id)
	if e := os.MkdirAll(job, 0700); e != nil {
		return s.Status(), e
	}
	success := false
	defer func() {
		if !success {
			s.removeJob(id)
		}
	}()
	file, e := os.OpenFile(filepath.Join(job, "upload.gwup"), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return s.Status(), e
	}
	count, e := io.Copy(file, io.LimitReader(r, UploadLimit+1))
	if e == nil {
		e = file.Sync()
	}
	ce := file.Close()
	if e == nil {
		e = ce
	}
	if e != nil {
		return s.Status(), e
	}
	if count > UploadLimit {
		return s.Status(), fmt.Errorf("升级文件不能超过32MiB")
	}
	if ctx.Err() != nil {
		return s.Status(), ctx.Err()
	}
	verified, e := VerifyBundle(filepath.Join(job, "upload.gwup"), s.path("config", "update-trust.json"), s.arch(), "")
	if e != nil {
		return s.Status(), e
	}
	current, e := s.Current()
	if e != nil {
		return s.Status(), e
	}
	cmp, e := CompareRelease(verified.Manifest.Release, current)
	if e != nil || cmp <= 0 {
		return s.Status(), fmt.Errorf("只能上传高于当前版本的升级包")
	}
	if target != "" && verified.Manifest.Release != target {
		return s.Status(), fmt.Errorf("过渡版请上传厂家提供的 %s 升级包", target)
	}
	if _, e = s.existingTarget(verified.Manifest); e != nil {
		return s.Status(), e
	}
	previous, oldErr := s.read()
	st := State{ID: id, Phase: "ready", Manifest: verified.Manifest, Previous: current, Message: "签名、摘要、型号及架构校验通过，等待人工确认"}
	if e = s.save(st); e != nil {
		return s.Status(), e
	}
	success = true
	if oldErr == nil && previous.Phase == "ready" {
		s.removeJob(previous.ID)
	}
	return s.Status(), nil
}
func (s *Service) lock(st State) error {
	lock := s.path("data", "install.lock")
	if e := os.Mkdir(lock, 0700); e != nil {
		return fmt.Errorf("安装维护正在进行或遗留锁须核查")
	}
	if e := config.WriteAtomic(filepath.Join(lock, "upgrade-owner"), []byte(st.ID)); e != nil {
		os.Remove(lock)
		return e
	}
	return nil
}
func (s *Service) unlock(id string) {
	lock := s.path("data", "install.lock")
	b, e := os.ReadFile(filepath.Join(lock, "upgrade-owner"))
	if e == nil && string(b) == id {
		if os.Remove(filepath.Join(lock, "upgrade-owner")) == nil {
			_ = os.Remove(lock)
		}
	}
}
func (s *Service) Begin(id, source string) error {
	if (&network.Service{Opts: network.Options{Base: s.Opts.Base}}).Busy() {
		return fmt.Errorf("网络试用未结束，不能升级")
	}
	if e := s.supported(); e != nil {
		return e
	}
	uploadLock := s.path("data", "upgrades", "upload.lock")
	if e := os.Mkdir(uploadLock, 0700); e != nil {
		return fmt.Errorf("正在上传或准备升级，稍后重试")
	}
	defer os.Remove(uploadLock)
	st, e := s.read()
	if e != nil || st.ID != id || st.Phase != "ready" {
		return fmt.Errorf("待升级记录已改变，请重新读取")
	}
	if e = s.lock(st); e != nil {
		return e
	}
	ok := false
	defer func() {
		if !ok {
			s.unlock(id)
		}
	}()
	job, _ := s.job(id)
	v, e := VerifyBundle(filepath.Join(job, "upload.gwup"), s.path("config", "update-trust.json"), s.arch(), "")
	if e != nil {
		return e
	}
	current, e := s.Current()
	if e != nil || current != st.Previous {
		return fmt.Errorf("当前版本已改变")
	}
	if v.Manifest.Release != st.Manifest.Release {
		return fmt.Errorf("升级内容已改变")
	}
	if e = s.checkSpace(s.needed(v.Manifest.UnpackedSize)); e != nil {
		return e
	}
	contextBytes, _ := json.Marshal(s.Opts)
	if e = config.WriteAtomic(filepath.Join(job, "context.json"), contextBytes); e != nil {
		return e
	}
	st.Source = source
	st.BootID = bootID()
	st.Phase = "queued"
	st.Message = "已确认维护窗口，升级执行器即将启动"
	if e = s.save(st); e != nil {
		return e
	}
	ok = true
	return nil
}
func (s *Service) Abort(id, msg string) error {
	st, e := s.read()
	if e != nil || st.ID != id || st.Phase != "queued" {
		return fmt.Errorf("升级状态不能撤销")
	}
	if e = s.phase(&st, "failed", msg); e != nil {
		return e
	}
	s.unlock(id)
	return nil
}
func (s *Service) ReconcileBoot() error {
	st, e := s.read()
	if os.IsNotExist(e) {
		return nil
	}
	if e != nil {
		return e
	}
	if active(st.Phase) && st.BootID != "" && st.BootID != bootID() {
		return s.phase(&st, "needs_attention", "设备在升级期间重新上电，须由厂家核查当前版本及升级记录；不自动重试")
	}
	return nil
}
func (s *Service) cleanupPayload(st State) {
	job, e := s.job(st.ID)
	if e != nil {
		return
	}
	_ = os.Remove(filepath.Join(job, "upload.gwup"))
}
func readOptions(base, id string) (*Service, State, error) {
	if !idPattern.MatchString(id) {
		return nil, State{}, fmt.Errorf("任务编号无效")
	}
	b, e := os.ReadFile(filepath.Join(base, "data", "upgrades", "jobs", id, "context.json"))
	if e != nil {
		return nil, State{}, e
	}
	var opts Options
	if e = strictJSON(b, &opts); e != nil {
		return nil, State{}, e
	}
	s, e := New(opts)
	if e != nil {
		return nil, State{}, e
	}
	b1, e := filepath.EvalSymlinks(base)
	if e != nil || s.Opts.Base != b1 {
		return nil, State{}, fmt.Errorf("任务根目录不一致")
	}
	st, e := s.read()
	if e != nil || st.ID != id {
		return nil, State{}, fmt.Errorf("任务记录不匹配")
	}
	return s, st, nil
}
func isMissing(err error) bool { return errors.Is(err, os.ErrNotExist) }

package update

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/storage"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

func (s *Service) Launch(id string) (*exec.Cmd, error) {
	st, e := s.read()
	if e != nil || st.ID != id || st.Phase != "queued" {
		return nil, fmt.Errorf("升级任务未就绪")
	}
	binary := s.path("releases", st.Previous, "bin", "gateway-updater")
	log, e := os.OpenFile(s.path("logs", "upgrade.log"), os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if e != nil {
		return nil, e
	}
	defer log.Close()
	cmd := exec.Command(nativeExecutable(binary), "worker", "--base", s.Opts.Base, "--id", id)
	cmd.Stdout = log
	cmd.Stderr = log
	detach(cmd)
	if e = cmd.Start(); e != nil {
		return nil, e
	}
	return cmd, nil
}

type WorkerOps struct {
	Service     func(context.Context, string, string) error
	CheckConfig func(context.Context, string) error
	Backup      func(string, string) error
	Probe       func(context.Context, string, string) error
	Delay       time.Duration
}

func (s *Service) defaultOps() WorkerOps {
	return WorkerOps{
		Service: func(ctx context.Context, release, action string) error {
			script := s.path("releases", release, "deploy", "service.sh")
			cmd := exec.CommandContext(ctx, s.Opts.Shell, script, action)
			cmd.Stdout = os.Stdout
			cmd.Stderr = os.Stderr
			return cmd.Run()
		},
		CheckConfig: func(ctx context.Context, release string) error {
			cmd := exec.CommandContext(ctx, nativeExecutable(s.path("releases", release, "bin", "gateway-core")), "--check-config", "--config", s.path("config", "gateway.json"))
			cmd.Stdout = os.Stdout
			cmd.Stderr = os.Stderr
			return cmd.Run()
		},
		Backup: storage.BackupDatabase, Probe: s.probe, Delay: 2 * time.Second}
}
func verifyInstalled(root, arch, release string) error {
	for name, value := range map[string]string{"RELEASE": release, "ARCH": arch, "NORTH_VERSION": "3.2", "UPDATE_VERSION": "1"} {
		b, e := os.ReadFile(filepath.Join(root, name))
		if e != nil || strings.TrimSpace(string(b)) != value {
			return fmt.Errorf("版本兼容标记不符: %s", name)
		}
	}
	b, e := os.ReadFile(filepath.Join(root, "SHA256SUMS"))
	if e != nil || len(b) > 16384 {
		return fmt.Errorf("版本摘要清单无效")
	}
	expected := map[string]bool{}
	for _, n := range RuntimeFiles {
		if n != "SHA256SUMS" {
			expected[n] = true
		}
	}
	for _, line := range strings.Split(strings.TrimSpace(string(b)), "\n") {
		parts := strings.SplitN(line, "  ", 2)
		if len(parts) != 2 || !expected[parts[1]] {
			return fmt.Errorf("版本摘要路径无效")
		}
		p := filepath.Join(root, filepath.FromSlash(parts[1]))
		info, e := os.Lstat(p)
		if e != nil || !info.Mode().IsRegular() {
			return fmt.Errorf("版本文件缺失或为链接")
		}
		sum, e := HashFile(p)
		if e != nil || sum != parts[0] {
			return fmt.Errorf("版本文件摘要不符: %s", parts[1])
		}
		delete(expected, parts[1])
	}
	if len(expected) != 0 {
		return fmt.Errorf("版本摘要缺少文件")
	}
	return nil
}
func (s *Service) switchTo(release string) error {
	if _, e := releaseParts(release); e != nil {
		return e
	}
	dest := s.path("releases", release)
	p, e := filepath.EvalSymlinks(dest)
	if e != nil || p != dest {
		return fmt.Errorf("目标版本路径无效")
	}
	tmp := s.path("current.upgrade-new")
	if _, e = os.Lstat(tmp); !os.IsNotExist(e) {
		return fmt.Errorf("发现未处理的current.upgrade-new")
	}
	if e = os.Symlink(dest, tmp); e != nil {
		return e
	}
	if e = replaceLink(tmp, s.path("current"), s.Opts.Shell); e != nil {
		os.Remove(tmp)
		return e
	}
	return syncDirectory(s.Opts.Base)
}
func (s *Service) backup(st State, ops WorkerOps) error {
	dir := s.path("data", "upgrade-backup-"+st.ID)
	if e := os.Mkdir(dir, 0700); e != nil {
		return e
	}
	for _, pair := range [][2]string{{"config/gateway.json", "gateway.json"}, {"config/service.env", "service.env"}, {"config/update-trust.json", "update-trust.json"}, {"data/admin.json", "admin.json"}, {"data/clock-state.json", "clock-state.json"}, {"config/license.json", "license.json"}} {
		b, e := os.ReadFile(s.path(filepath.FromSlash(pair[0])))
		if os.IsNotExist(e) && (pair[1] == "clock-state.json" || pair[1] == "license.json") {
			continue
		}
		if e != nil {
			return e
		}
		if e = config.WriteAtomic(filepath.Join(dir, pair[1]), b); e != nil {
			return e
		}
	}
	if _, e := os.Stat(s.path("data", "gateway-v2.db")); e == nil {
		if e = ops.Backup(s.path("data", "gateway-v2.db"), filepath.Join(dir, "gateway-v2.db")); e != nil {
			return e
		}
	} else {
		return e
	}
	b, _ := json.Marshal(st)
	return config.WriteAtomic(filepath.Join(dir, "upgrade.json"), b)
}
func (s *Service) Run(id string, ops *WorkerOps) error {
	releaseExecution, e := s.execution(id, false)
	if e != nil {
		return e
	}
	defer releaseExecution()
	st, e := s.read()
	if e != nil || st.ID != id || st.Phase != "queued" {
		return fmt.Errorf("升级任务已变化，不重复执行")
	}
	if st.BootID != bootID() {
		return fmt.Errorf("任务来自旧启动，须人工恢复")
	}
	actual := s.defaultOps()
	if ops != nil {
		actual = *ops
	}
	st.PID = os.Getpid()
	if e = s.phase(&st, "preparing", "正在重新校验升级包和原版本"); e != nil {
		return e
	}
	if actual.Delay > 0 {
		time.Sleep(actual.Delay)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Minute)
	defer cancel()
	stopped, switched := false, false
	fail := func(cause error) error {
		fmt.Fprintln(os.Stderr, "Upgrade failed:", cause)
		if !stopped && !switched {
			if e := s.phase(&st, "failed", "升级准备失败，原程序未切换: "+cause.Error()); e != nil {
				return e
			}
			s.unlock(id)
			return cause
		}
		if e := s.phase(&st, "rolling_back", "新版本未通过检查，正在恢复兼容的原程序"); e != nil {
			return e
		}
		recoveryCtx, stop := context.WithTimeout(context.Background(), 2*time.Minute)
		defer stop()
		oldRoot := s.path("releases", st.Previous)
		er := verifyInstalled(oldRoot, s.arch(), st.Previous)
		current, _ := s.Current()
		if er == nil && current != "" {
			er = actual.Service(recoveryCtx, current, "stop")
		}
		if er == nil {
			er = s.switchTo(st.Previous)
		}
		if er == nil {
			er = actual.Service(recoveryCtx, st.Previous, "start")
		}
		if er == nil {
			er = actual.Probe(recoveryCtx, st.Previous, st.ID)
		}
		if er != nil {
			_ = s.phase(&st, "needs_attention", "升级及自动恢复未完成，请厂家核查；保留数据库和升级记录: "+er.Error())
			return fmt.Errorf("%w; recovery: %v", cause, er)
		}
		if e := s.phase(&st, "rolled_back", "新版本未通过检查，已恢复原程序；未恢复旧数据库，未重发仪表动作"); e != nil {
			return e
		}
		s.unlock(id)
		s.cleanupPayload(st)
		return cause
	}
	job, _ := s.job(id)
	verified, e := VerifyBundle(filepath.Join(job, "upload.gwup"), s.path("config", "update-trust.json"), s.arch(), "")
	if e != nil {
		return fail(e)
	}
	current, e := s.Current()
	if e != nil || current != st.Previous || verified.Manifest.Release != st.Manifest.Release {
		return fail(fmt.Errorf("当前版本或升级内容已改变"))
	}
	if e = verifyInstalled(s.path("releases", st.Previous), s.arch(), st.Previous); e != nil {
		return fail(e)
	}
	target := s.path("releases", st.Manifest.Release)
	reuse, e := s.existingTarget(verified.Manifest)
	if e != nil {
		return fail(e)
	}
	if e = s.checkSpace(s.needed(st.Manifest.UnpackedSize)); e != nil {
		return fail(e)
	}
	if !reuse {
		stage := filepath.Join(job, "unpacked")
		if e = os.Mkdir(stage, 0700); e != nil {
			return fail(e)
		}
		if _, e = VerifyBundle(filepath.Join(job, "upload.gwup"), s.path("config", "update-trust.json"), s.arch(), stage); e != nil {
			return fail(e)
		}
		if e = verifyInstalled(stage, s.arch(), st.Manifest.Release); e != nil {
			return fail(e)
		}
		if e = os.Rename(stage, target); e != nil {
			return fail(e)
		}
		if e = syncDirectory(s.path("releases")); e != nil {
			return fail(e)
		}
	}
	if e = actual.CheckConfig(ctx, st.Manifest.Release); e != nil {
		return fail(e)
	}
	// The core gate is persisted before this process starts. Confirm it is still
	// held and no unresolved control appeared before stopping the measurement service.
	if e = s.phase(&st, "backing_up", "正在停止采集服务并备份配置、账号、时钟和数据库"); e != nil {
		return e
	}
	stopped = true
	if e = actual.Service(ctx, st.Previous, "stop"); e != nil {
		return fail(e)
	}
	if e = s.backup(st, actual); e != nil {
		return fail(e)
	}
	if e = s.phase(&st, "switching", "备份完成，正在原子切换程序版本"); e != nil {
		return e
	}
	if e = s.switchTo(st.Manifest.Release); e != nil {
		return fail(e)
	}
	switched = true
	if e = s.phase(&st, "starting", "正在启动新版本，网页及Modbus通讯将恢复"); e != nil {
		return e
	}
	if e = actual.Service(ctx, st.Manifest.Release, "start"); e != nil {
		return fail(e)
	}
	if e = s.phase(&st, "verifying", "正在核对实际运行版本、网页和采集调度"); e != nil {
		return e
	}
	if e = actual.Probe(ctx, st.Manifest.Release, st.ID); e != nil {
		return fail(e)
	}
	if e = s.phase(&st, "succeeded", "升级成功；配置、账号、历史和控制记录保留，请核对现场数据"); e != nil {
		return e
	}
	s.unlock(id)
	s.cleanupPayload(st)
	return nil
}
func RunWorker(base, id string) error {
	s, _, e := readOptions(base, id)
	if e != nil {
		return e
	}
	return s.Run(id, nil)
}
func RunRecovery(base, id string) error {
	s, st, e := readOptions(base, id)
	if e != nil {
		return e
	}
	if !active(st.Phase) {
		return fmt.Errorf("任务已结束，不重复恢复")
	}
	if st.BootID == bootID() && workerAlive(st.PID, id) {
		return fmt.Errorf("升级执行器仍在运行")
	}
	if st.Phase == "queued" && time.Since(time.UnixMilli(st.UpdatedMS)) < 30*time.Second {
		return fmt.Errorf("任务刚受理，请等待执行器启动，不提前恢复")
	}
	releaseExecution, e := s.execution(id, true)
	if e != nil {
		return e
	}
	defer releaseExecution()
	owner, e := os.ReadFile(s.path("data", "install.lock", "upgrade-owner"))
	if e != nil || string(owner) != id {
		return fmt.Errorf("升级锁归属不明，不能自动解除")
	}
	current, e := s.Current()
	if e != nil || current != st.Previous && current != st.Manifest.Release {
		return fmt.Errorf("当前版本不是原版本或目标版本，须人工处理")
	}
	if e = verifyInstalled(s.path("releases", current), s.arch(), current); e != nil {
		return e
	}
	st.PID = os.Getpid()
	st.BootID = bootID()
	if e = s.phase(&st, "verifying", "厂家已发起断电后的当前版本检查，不重放升级或仪表动作"); e != nil {
		return e
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	if e = s.probe(ctx, current, id); e != nil {
		_ = s.phase(&st, "needs_attention", "当前程序未通过恢复检查: "+e.Error())
		return e
	}
	phase := "succeeded"
	if current == st.Previous {
		phase = "rolled_back"
	}
	if e = s.phase(&st, phase, "当前版本恢复检查已通过，未重复安装或发送仪表动作"); e != nil {
		return e
	}
	s.unlock(id)
	s.cleanupPayload(st)
	return nil
}
func (s *Service) coreRequest(ctx context.Context, path string) (map[string]any, error) {
	transport := &http.Transport{}
	base := "http://" + s.Opts.IPC
	if strings.HasPrefix(s.Opts.IPC, "unix:") {
		socket := strings.TrimPrefix(s.Opts.IPC, "unix:")
		transport.DialContext = func(c context.Context, _, _ string) (net.Conn, error) {
			return (&net.Dialer{}).DialContext(c, "unix", socket)
		}
		base = "http://gateway"
	} else {
		host, _, e := net.SplitHostPort(s.Opts.IPC)
		if e != nil || net.ParseIP(host) == nil || !net.ParseIP(host).IsLoopback() {
			return nil, fmt.Errorf("升级检查IPC必须为本机")
		}
	}
	defer transport.CloseIdleConnections()
	token, e := os.ReadFile(s.path("data", "ipc.token"))
	if e != nil {
		return nil, e
	}
	req, e := http.NewRequestWithContext(ctx, "GET", base+path, nil)
	if e != nil {
		return nil, e
	}
	req.Header.Set("Authorization", "Bearer "+strings.TrimSpace(string(token)))
	response, e := (&http.Client{Transport: transport, Timeout: 3 * time.Second}).Do(req)
	if e != nil {
		return nil, e
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return nil, fmt.Errorf("采集服务检查返回%d", response.StatusCode)
	}
	var out map[string]any
	e = json.NewDecoder(io.LimitReader(response.Body, 65536)).Decode(&out)
	return out, e
}
func (s *Service) probe(ctx context.Context, release, id string) error {
	budget := 90 * time.Second
	if s.Opts.Fixture {
		budget = 12 * time.Second
	}
	ctx, cancel := context.WithTimeout(ctx, budget)
	defer cancel()
	parts, e := releaseParts(release)
	if e != nil {
		return e
	}
	software := fmt.Sprintf("%d.%d.%d", parts[0], parts[1], parts[2])
	u, e := url.Parse(s.Opts.WebURL)
	if e != nil || u.Scheme != "http" && u.Scheme != "https" {
		return fmt.Errorf("网页检查地址无效")
	}
	ip := net.ParseIP(u.Hostname())
	if ip == nil {
		return fmt.Errorf("网页检查只允许本机IP")
	}
	local := ip.IsLoopback()
	addresses, _ := net.InterfaceAddrs()
	for _, a := range addresses {
		p, _, _ := net.ParseCIDR(a.String())
		if p != nil && p.Equal(ip) {
			local = true
		}
	}
	if !local {
		return fmt.Errorf("网页检查地址不是本机网口")
	}
	transport := &http.Transport{}
	if u.Scheme == "https" {
		b, e := os.ReadFile(s.Opts.TLSCert)
		if e != nil {
			return e
		}
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM(b) {
			return fmt.Errorf("HTTPS证书无效")
		}
		transport.TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
	}
	defer transport.CloseIdleConnections()
	web := &http.Client{Transport: transport, Timeout: 3 * time.Second}
	var last error
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		status, e := s.coreRequest(ctx, "/api/maintenance/upgrade/check")
		if e == nil {
			if status["healthy"] == true && status["idle"] == true && status["software"] == software && status["running_release"] == release && status["job_id"] == id && status["north_ok"] == true {
				req, _ := http.NewRequestWithContext(ctx, "GET", s.Opts.WebURL, nil)
				response, er := web.Do(req)
				if er == nil {
					io.Copy(io.Discard, io.LimitReader(response.Body, 65536))
					response.Body.Close()
					if response.StatusCode == 200 && response.Header.Get("X-Gateway-Web-Version") == software && response.Header.Get("X-Gateway-Running-Release") == release {
						return nil
					}
					last = fmt.Errorf("网页运行版本未匹配")
				} else {
					last = er
				}
			} else {
				last = fmt.Errorf("采集服务、控制空闲状态或实际运行版本未通过检查")
			}
		} else {
			last = e
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("启动验证超时: %v", last)
		case <-ticker.C:
		}
	}
}
func WebURL(listen, cert string) (string, error) {
	host, port, e := net.SplitHostPort(listen)
	if e != nil {
		return "", e
	}
	if host == "" || host == "0.0.0.0" || host == "::" {
		host = "127.0.0.1"
	}
	if net.ParseIP(host) == nil {
		return "", fmt.Errorf("网页监听须为IP")
	}
	if _, e = strconv.Atoi(port); e != nil {
		return "", e
	}
	scheme := "http"
	if cert != "" {
		scheme = "https"
	}
	return scheme + "://" + net.JoinHostPort(host, port) + "/", nil
}

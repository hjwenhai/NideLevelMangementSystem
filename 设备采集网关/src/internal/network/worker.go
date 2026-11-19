package network

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/config"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"time"
)

type WorkerOps struct {
	Stop  func() error
	Start func() error
	Apply func(Settings) error
}

func (s *Service) Ops(shell, script string) WorkerOps {
	service := func(action string) error {
		ctx, cancel := context.WithTimeout(context.Background(), 75*time.Second)
		defer cancel()
		cmd := exec.CommandContext(ctx, shell, script, action)
		cmd.Stdout = os.Stdout
		cmd.Stderr = os.Stderr
		return cmd.Run()
	}
	return WorkerOps{Stop: func() error { return service("network-stop") }, Start: func() error { return service("network-start") }, Apply: s.applyOS}
}
func (s *Service) changeFiles(st State) error {
	if len(st.Files) != 4 {
		return fmt.Errorf("网络事务备份不完整")
	}
	for i, p := range s.paths() {
		if st.Files[i].Path != p {
			return fmt.Errorf("网络事务备份路径不匹配")
		}
		if _, e := regular(p); e != nil {
			return e
		}
	}
	for i, p := range st.Settings.Interfaces {
		if e := config.WriteAtomic(s.paths()[i], VendorFile(st.Files[i].Data, p)); e != nil {
			return e
		}
	}
	cfg, e := config.Parse(st.Files[2].Data)
	if e != nil {
		return e
	}
	_, port, e := net.SplitHostPort(cfg.TCP.Listen)
	if e != nil {
		return e
	}
	cfg.TCP.Listen = listenFor(st.Settings, port)
	if cfg.Revision == ^uint32(0) {
		return fmt.Errorf("配置版本已耗尽")
	}
	cfg.Revision++
	if e = config.SaveAtomic(s.paths()[2], cfg); e != nil {
		return e
	}
	env, e := setWeb(st.Files[3].Data)
	if e != nil {
		return e
	}
	return config.WriteAtomic(s.paths()[3], env)
}
func (s *Service) restore(st State, apply func(Settings) error) error {
	if len(st.Files) != 4 {
		return fmt.Errorf("备份不完整")
	}
	if e := st.Before.Validate(); e != nil {
		return e
	}
	for i, p := range s.paths() {
		if st.Files[i].Path != p {
			return fmt.Errorf("恢复路径不匹配")
		}
		if _, e := regular(p); e != nil {
			return e
		}
	}
	for _, f := range st.Files {
		old, _ := regular(f.Path)
		if bytes.Equal(old, f.Data) {
			continue
		}
		if e := config.WriteAtomic(f.Path, f.Data); e != nil {
			return e
		}
	}
	return apply(st.Before)
}
func (s *Service) event(kind string, st State) {
	path := s.path("logs", "network.log")
	_ = os.MkdirAll(filepath.Dir(path), 0700)
	if info, e := os.Stat(path); e == nil && info.Size() > 1048576 {
		_ = os.Rename(path, path+".1")
	}
	f, e := os.OpenFile(path, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0600)
	if e != nil {
		return
	}
	defer f.Close()
	b, _ := json.Marshal(map[string]any{"time": time.Now().Format(time.RFC3339), "event": kind, "id": st.ID, "source": st.Source, "confirmed_by": st.ConfirmSource, "message": st.Message, "settings": st.Settings})
	_, _ = f.Write(append(b, '\n'))
	_ = f.Sync()
}
func (s *Service) rollback(st State, ops WorkerOps, reason string) error {
	st.Phase = "rolling_back"
	st.Message = reason + "，正在恢复原网络"
	// The previous durable phase already blocks writes. Even if recording the
	// rollback phase fails, attempt restoration from the in-memory snapshot.
	_ = s.save(st)
	e := ops.Stop()
	if e == nil {
		e = s.restore(st, ops.Apply)
	}
	if e == nil {
		e = ops.Start()
	}
	if e != nil {
		st.Phase = "failed"
		st.Message = "恢复未完成，请使用FUN临时地址或Console检查网络备份和日志"
		_ = s.save(st)
		s.event("rollback_failed", st)
		return e
	}
	st.Phase = "rolled_back"
	st.Message = reason + "，已恢复修改前参数"
	st.DeadlineMS = 0
	st.PID = 0
	if e = s.save(st); e != nil {
		return e
	}
	s.event("rolled_back", st)
	return nil
}

// RunWorker is independent of both application supervisors. The durable journal
// survives crashes; a pending trial is never promoted to confirmed on boot.
func (s *Service) RunWorker(id string, ops WorkerOps, duration time.Duration) error {
	var st State
	e := s.locked(func() error {
		var e error
		st, e = s.read()
		if e != nil {
			return e
		}
		if st.ID != id || st.Phase != "queued" || s.ownerAlive(st) {
			return fmt.Errorf("网络任务已改变或执行器已存在")
		}
		st.PID = os.Getpid()
		st.Boot = bootID()
		return s.save(st)
	})
	if e != nil {
		return e
	}
	time.Sleep(2 * time.Second)
	e = s.locked(func() error {
		current, e := s.read()
		if e != nil {
			return e
		}
		if current.ID != id || current.Phase != "queued" {
			return fmt.Errorf("网络任务已取消")
		}
		st = current
		st.Phase = "applying"
		st.Message = "正在切换双网口及通讯监听地址"
		if e = s.save(st); e != nil {
			return e
		}
		s.event("applying", st)
		return nil
	})
	if e != nil {
		return e
	}
	e = ops.Stop()
	if e == nil {
		e = s.changeFiles(st)
	}
	if e == nil {
		e = ops.Apply(st.Settings)
	}
	if e != nil {
		return s.locked(func() error { return s.rollback(st, ops, "网络应用失败") })
	}
	// Start the monotonic deadline before bringing services back. A failed start
	// consumes the same bounded trial rather than leaving an unlimited new IP.
	started := time.Now()
	st.Phase = "trial"
	st.DeadlineMS = time.Now().Add(duration).UnixMilli()
	st.Message = "请通过新IP登录并确认；超时或试用期间重启将恢复原配置"
	if e = s.locked(func() error { return s.save(st) }); e != nil {
		return s.locked(func() error { return s.rollback(st, ops, "试用状态保存失败") })
	}
	if e = ops.Start(); e != nil {
		return s.locked(func() error { return s.rollback(st, ops, "新服务启动失败") })
	}
	ticker := time.NewTicker(200 * time.Millisecond)
	defer ticker.Stop()
	for range ticker.C {
		done := false
		e = s.locked(func() error {
			current, e := s.read()
			if e != nil {
				done = true
				return s.rollback(st, ops, "网络试用状态读取失败")
			}
			if current.ID != id {
				return fmt.Errorf("网络任务编号改变")
			}
			st = current
			if time.Since(started) >= duration {
				done = true
				return s.rollback(st, ops, "试用超时未确认")
			}
			if st.Phase == "confirming" {
				st.Phase = "confirmed"
				st.Message = "网络设置已确认并保存，断电重启后继续使用"
				st.DeadlineMS = 0
				st.PID = 0
				if e = s.save(st); e != nil {
					done = true
					return s.rollback(st, ops, "网络确认保存失败")
				}
				s.event("confirmed", st)
				done = true
				return nil
			}
			if st.Phase != "trial" {
				return fmt.Errorf("网络试用状态异常")
			}
			return nil
		})
		if e != nil {
			return e
		}
		if done {
			return nil
		}
	}
	return nil
}
func bootID() string {
	b, _ := os.ReadFile("/proc/sys/kernel/random/boot_id")
	return strings.TrimSpace(string(b))
}
func (s *Service) ownerAlive(st State) bool {
	if st.PID <= 0 {
		return false
	}
	if runtime.GOOS != "linux" {
		return fixtureProcessAlive(st.PID)
	}
	if st.Boot == "" || st.Boot != bootID() {
		return false
	}
	b, _ := os.ReadFile(fmt.Sprintf("/proc/%d/cmdline", st.PID))
	parts := strings.Split(string(b), "\x00")
	worker, base := false, false
	for i, p := range parts {
		worker = worker || p == "--network-worker"
		if p == "--network-base" && i+1 < len(parts) {
			base = parts[i+1] == s.Opts.Base
		}
	}
	return worker && base
}

// RecoverBoot is called before service supervisors start. It does not restart
// processes and never treats an old trial's wall-clock deadline as confirmation.
func (s *Service) RecoverBoot() error {
	return s.locked(func() error {
		st, e := s.read()
		if e != nil {
			return e
		}
		if !pending(st.Phase) {
			return nil
		}
		if s.ownerAlive(st) {
			return fmt.Errorf("独立网络任务仍在运行，请等待任务结束")
		}
		if e = s.restore(st, s.applyOS); e != nil {
			st.Phase = "failed"
			st.Message = "启动时恢复网络失败，请检查备份与Console"
			_ = s.save(st)
			return e
		}
		st.Phase = "rolled_back"
		st.PID = 0
		st.DeadlineMS = 0
		st.Message = "检测到未确认或中断的网络修改，已恢复修改前配置"
		if e = s.save(st); e != nil {
			return e
		}
		s.event("boot_rollback", st)
		return nil
	})
}

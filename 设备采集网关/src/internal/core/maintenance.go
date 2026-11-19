package core

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/command"
	"sync"
	"sync/atomic"
	"time"
)

type ProgramRestart struct {
	Supported bool   `json:"supported"`
	Pending   bool   `json:"pending"`
	ID        string `json:"id"`
	Error     string `json:"error,omitempty"`
}
type restartState struct {
	mu      sync.Mutex
	pending atomic.Bool
	status  ProgramRestart
	launch  func() (func() error, error)
}

func (m *Manager) ConfigureProgramRestart(launch func() (func() error, error)) {
	m.restart.mu.Lock()
	defer m.restart.mu.Unlock()
	m.restart.launch = launch
	m.restart.status.Supported = launch != nil
}
func (m *Manager) ProgramRestartStatus() ProgramRestart {
	m.restart.mu.Lock()
	defer m.restart.mu.Unlock()
	return m.restart.status
}
func (m *Manager) RequestProgramRestart(source string) (ProgramRestart, error) {
	m.applyMu.Lock()
	defer m.applyMu.Unlock()
	m.restart.mu.Lock()
	defer m.restart.mu.Unlock()
	if m.restart.status.Pending {
		return m.restart.status, nil
	}
	if m.closing.Load() || m.upgrade.BlocksWrites() || m.network.Busy() {
		return m.restart.status, fmt.Errorf("程序正在退出")
	}
	if m.restart.launch == nil {
		return m.restart.status, fmt.Errorf("当前启动方式未启用网页程序重启，请使用部署服务启动")
	}
	if m.Engine.Status() != "" || m.Store.Status() != "" {
		return m.restart.status, fmt.Errorf("存储或控制恢复异常，拒绝网页重启，请先检查诊断")
	}
	for _, slot := range m.Engine.Results() {
		for _, c := range []command.Cycle{slot.Main, slot.Stop} {
			if c.State == command.Accepted || c.State == command.Running {
				return m.restart.status, fmt.Errorf("仪表%d存在正在受理或执行的控制，暂不能重启", slot.Number)
			}
		}
	}
	m.clockMu.Lock()
	err := m.clock.Checkpoint()
	m.clockMu.Unlock()
	if err != nil {
		return m.restart.status, fmt.Errorf("保存时钟状态失败，未安排重启")
	}
	idBytes := make([]byte, 16)
	if _, err = rand.Read(idBytes); err != nil {
		return m.restart.status, err
	}
	id := hex.EncodeToString(idBytes)
	audit, _ := json.Marshal(map[string]any{"id": id, "source": source, "requested_ms": time.Now().UnixMilli(), "boot": fmt.Sprintf("%016x", m.Boot)})
	if err = m.Store.Event("program_restart_requested", string(audit)); err != nil {
		return m.restart.status, fmt.Errorf("保存重启记录失败，未安排重启")
	}
	m.restart.pending.Store(true)
	m.restart.status = ProgramRestart{Supported: true, Pending: true, ID: id}
	wait, err := m.restart.launch()
	if err != nil {
		m.restart.pending.Store(false)
		m.restart.status.Pending = false
		m.restart.status.Error = "启动重启任务失败，请检查维护日志"
		_ = m.Store.Event("program_restart_failed", id+": launch failed")
		return m.restart.status, fmt.Errorf("启动重启任务失败")
	}
	go func() {
		err := wait()
		m.restart.mu.Lock()
		defer m.restart.mu.Unlock()
		if m.restart.status.ID != id || m.closing.Load() {
			return
		}
		// A successful worker stops this process. If it returns to a live old core,
		// do not keep control locked indefinitely or silently retry the restart.
		m.restart.pending.Store(false)
		m.restart.status.Pending = false
		m.restart.status.Error = "重启任务已返回但当前程序仍在运行，请检查维护日志"
		if err != nil {
			m.restart.status.Error = "重启任务执行失败，请检查维护日志"
		}
		_ = m.Store.Event("program_restart_failed", id+": worker did not replace current process")
	}()
	return m.restart.status, nil
}

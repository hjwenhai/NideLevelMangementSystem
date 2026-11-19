package core

import (
	"context"
	"fmt"
	"levelgateway/src/internal/clockstate"
	"levelgateway/src/internal/config"
	"os"
	"time"
)

func (m *Manager) Healthy() bool {
	m.mu.RLock()
	defer m.mu.RUnlock()
	now := time.Since(m.Started).Milliseconds()
	if started := m.ApplyingSinceMS.Load(); started > 0 && now-(started-1) > 60000 {
		return false
	}
	if m.cfg.Mode == "maintenance" {
		return true
	}
	for _, rt := range m.Ports {
		if rt.Port.Role != config.South {
			continue
		}
		rt.mu.Lock()
		closing := rt.closing
		failed := rt.Error != ""
		rt.mu.Unlock()
		if closing {
			continue
		}
		if failed || now-rt.Progress.Load() > 60000 {
			return false
		}
	}
	return true
}
func (m *Manager) MaintainClock(ctx context.Context) {
	anchor := time.Now()
	generation := m.ClockGeneration.Load()
	ticker := time.NewTicker(time.Second)
	checkpoint := time.Now()
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case now := <-ticker.C:
			m.clockMu.Lock()
			if latest := m.ClockGeneration.Load(); latest != generation {
				anchor = now
				generation = latest
				m.clockMu.Unlock()
				continue
			}
			wallDelta := now.UnixMilli() - anchor.UnixMilli()
			monoDelta := now.Sub(anchor).Milliseconds()
			if d := wallDelta - monoDelta; d > 2000 || d < -2000 {
				m.TimeTrusted.Store(false)
				m.Cache.InvalidateTimeQuality()
				if err := m.clock.Invalidate("检测到系统时钟跳变，请重新校核时间"); err != nil {
					_ = m.Store.Event("time", "保存时间失效记录失败："+err.Error())
				}
				_ = m.Store.Event("time", "检测到系统时钟跳变，重新标记时间未确认")
				anchor = now
			}
			if time.Since(checkpoint) >= time.Minute {
				if err := m.clock.Checkpoint(); err != nil {
					m.TimeTrusted.Store(false)
					m.Cache.InvalidateTimeQuality()
					_ = m.clock.Invalidate("保存时间检查记录失败：" + err.Error())
					_ = m.Store.Event("time", m.clock.Status.Reason)
				}
				checkpoint = time.Now()
			}
			m.clockMu.Unlock()
		}
	}
}

func (m *Manager) StartClockMonitor() {
	m.wg.Add(1)
	go func() { defer m.wg.Done(); m.MaintainClock(m.ctx) }()
}
func (m *Manager) CalibrateClock(utcMS int64, allowSet bool) error {
	m.clockMu.Lock()
	defer m.clockMu.Unlock()
	m.TimeTrusted.Store(false)
	m.Cache.InvalidateTimeQuality()
	err := m.clock.Calibrate(utcMS, allowSet)
	m.TimeTrusted.Store(m.clock.Status.Trusted)
	m.ClockGeneration.Add(1)
	if err != nil {
		return err
	}
	return m.Store.Event("time", m.clock.Status.Reason+"；"+m.clock.Status.Warning)
}

func (m *Manager) ClockStatus() clockstate.Status {
	m.clockMu.Lock()
	defer m.clockMu.Unlock()
	return m.clock.Status
}

// A watchdog can arm on open; enable only after verifying the board driver.
func (m *Manager) Watchdog(ctx context.Context, path string) error {
	if m.Simulate {
		return fmt.Errorf("模拟模式不打开硬件看门狗")
	}
	f, err := os.OpenFile(path, os.O_WRONLY, 0)
	if err != nil {
		return err
	}
	defer func() {
		if ctx.Err() != nil {
			_, _ = f.Write([]byte("V"))
		}
		_ = f.Close()
	}()
	tick := time.NewTicker(5 * time.Second)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil
		case <-tick.C:
			if !m.Healthy() {
				return fmt.Errorf("core采集调度超过60秒无进展，停止喂狗")
			}
			if _, err = f.Write([]byte{0}); err != nil {
				return err
			}
		}
	}
}

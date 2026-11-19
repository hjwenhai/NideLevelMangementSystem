package core

import (
	"context"
	"fmt"
	"levelgateway/src/internal/cache"
	"levelgateway/src/internal/platform"
	"time"
)

// Modes: 0 off, 1 steady on, 2 slow blink (2s), 3 fast blink (0.5s), 4 heartbeat (1s).
type IndicatorStatus struct {
	Modes         [3]int            `json:"modes"`
	Enabled       bool              `json:"driver_enabled"`
	Error         string            `json:"driver_error,omitempty"`
	Reasons       []IndicatorReason `json:"reasons"`
	EnabledMeters int               `json:"enabled_meters"`
	GoodMeters    int               `json:"good_meters"`
}

type IndicatorReason struct {
	Severity string `json:"severity"`
	Message  string `json:"message"`
}

func indicatorMeterGood(v cache.View) bool {
	return v.Valid == 127 && v.Quality&2 != 0 && v.Quality&(4|8|32|256) == 0
}

func indicatorModes(views []cache.View, maintenance, healthy, trusted, critical bool) [3]int {
	s := [3]int{4, 0, 0}
	if !healthy {
		s[0] = 1
		critical = true
	}
	enabled, good := 0, 0
	warning := !trusted
	for _, v := range views {
		if !v.Config.Enabled {
			continue
		}
		enabled++
		if indicatorMeterGood(v) {
			good++
		}
		if v.Quality&8 != 0 {
			critical = true
		}
		if v.Quality&128 != 0 {
			warning = true
		}
		if v.ParameterError != "" {
			warning = true
		}
	}
	if !maintenance && enabled > 0 {
		if good == enabled {
			s[1] = 1
		} else if good > 0 {
			s[1] = 2
			warning = true
		} else {
			s[1] = 3
			warning = true
		}
	}
	if critical {
		s[2] = 1
	} else if warning {
		s[2] = 2
	}
	return s
}
func IndicatorOn(mode int, elapsed time.Duration) bool {
	switch mode {
	case 1:
		return true
	case 2:
		return elapsed%(2*time.Second) < time.Second
	case 3:
		return elapsed%(500*time.Millisecond) < 250*time.Millisecond
	case 4:
		return elapsed%time.Second < 500*time.Millisecond
	}
	return false
}
func (m *Manager) IndicatorStatus() IndicatorStatus {
	c := m.Config()
	healthy := m.Healthy()
	critical := !m.license.Allowed() || m.Store.Status() != "" || m.Engine.Status() != ""
	reasons := []IndicatorReason{}
	add := func(severity, message string) { reasons = append(reasons, IndicatorReason{severity, message}) }
	if !healthy {
		add("critical", "核心调度健康检查异常，请查看运行事件与端口状态")
	}
	if !m.license.Allowed() {
		add("critical", m.license.Status().Message)
	}
	if err := m.Store.Status(); err != "" {
		add("critical", "存储异常："+err)
	}
	if err := m.Engine.Status(); err != "" {
		add("critical", "控制记录恢复或持久化异常："+err)
	}
	m.mu.RLock()
	for _, rt := range m.Ports {
		rt.mu.Lock()
		critical = critical || rt.Error != ""
		if rt.Error != "" {
			add("critical", fmt.Sprintf("COM%d 串口或运行异常：%s", rt.Port.ID, rt.Error))
		}
		rt.mu.Unlock()
	}
	m.mu.RUnlock()
	for _, r := range m.Engine.Results() {
		for _, item := range []struct {
			name    string
			state   uint16
			message string
		}{
			{"动作", uint16(r.Main.State), r.Main.Message}, {"独立停止", uint16(r.Stop.State), r.Stop.Message},
		} {
			if item.state == 4 || item.state == 5 {
				critical = true
				outcome := "失败"
				if item.state == 5 {
					outcome = "结果不确定"
				}
				add("critical", fmt.Sprintf("槽位%d %s%s：%s。请在控制结果中核查处理", r.Number, item.name, outcome, item.message))
			}
		}
	}
	m.ledMu.RLock()
	s := m.led
	m.ledMu.RUnlock()
	if s.Error != "" {
		add("critical", "实体指示灯驱动异常："+s.Error)
	}
	trusted := m.TimeTrusted.Load()
	if !trusted {
		add("warning", "网关时间尚未确认，请核对时间状态并校时")
	}
	views := m.Views()
	s.EnabledMeters, s.GoodMeters = 0, 0
	for _, v := range views {
		if !v.Config.Enabled {
			continue
		}
		s.EnabledMeters++
		if indicatorMeterGood(v) {
			s.GoodMeters++
		}
		reasons = append(reasons, meterIndicatorReasons(v, c.Mode == "maintenance", c.FastMS, trusted)...)
	}
	s.Reasons = reasons
	s.Modes = indicatorModes(views, c.Mode == "maintenance", healthy, trusted, critical || s.Error != "")
	return s
}

func meterIndicatorReasons(v cache.View, maintenance bool, fastMS int, trusted bool) []IndicatorReason {
	reasons := []IndicatorReason{}
	label := fmt.Sprintf("COM%d / 地址%d · 槽位%d（%s）", v.Config.Port, v.Config.Address, v.Config.Slot, v.Config.Name)
	if v.Quality&8 != 0 {
		reasons = append(reasons, IndicatorReason{"critical", fmt.Sprintf("%s：仪表报告故障字 0x%04X，请结合故障字有效位及数据年龄核查", label, v.Raw[52])})
	}
	if v.ParameterError != "" {
		reasons = append(reasons, IndicatorReason{"warning", label + "：参数快照采集失败，" + v.ParameterError})
	}
	if trusted && v.Quality&128 != 0 {
		reasons = append(reasons, IndicatorReason{"warning", label + "：部分采集数据的时间尚未确认，等待重新采集"})
	}
	if !maintenance && !indicatorMeterGood(v) {
		groups := [...]string{"液位及变化率 IR0～5", "温度状态及平均温度 IR22～26", "密度、浮子状态及故障 IR49～52"}
		for i, name := range groups {
			g := v.Groups[i]
			message := ""
			switch {
			case g.Unsupported:
				message = "寄存器不支持"
			case g.Error != "":
				message = "最近一次采集失败，" + g.Error
			case g.TimeMS == 0:
				message = "尚未成功采集"
			case g.AgeMS > uint32(3*fastMS):
				message = fmt.Sprintf("数据过期，最近成功数据距今 %.1f 秒", float64(g.AgeMS)/1000)
			}
			if message != "" {
				reasons = append(reasons, IndicatorReason{"warning", label + " · " + name + "：" + message})
			}
		}
	}
	return reasons
}
func (m *Manager) StartIndicators() {
	m.wg.Add(1)
	go func() { defer m.wg.Done(); m.RunIndicators(m.ctx) }()
}
func (m *Manager) RunIndicators(ctx context.Context) {
	if m.Simulate {
		return
	} // API still exposes virtual modes; never open host devices.
	d, err := platform.OpenLEDs()
	setError := func(err error) {
		m.ledMu.Lock()
		m.led.Enabled = false
		m.led.Error = err.Error()
		m.ledMu.Unlock()
		_ = m.Store.Event("indicator", err.Error()+"；采集继续，灯驱动须核验")
	}
	if err != nil {
		setError(err)
		return
	}
	defer func() { d.Close(); m.ledMu.Lock(); m.led.Enabled = false; m.ledMu.Unlock() }()
	m.ledMu.Lock()
	m.led.Enabled = true
	m.led.Error = ""
	m.ledMu.Unlock()
	tick := time.NewTicker(100 * time.Millisecond)
	defer tick.Stop()
	for {
		s := m.IndicatorStatus()
		var on [3]bool
		for i, mode := range s.Modes {
			on[i] = IndicatorOn(mode, time.Since(m.Started))
		}
		if err = d.Set(on); err != nil {
			setError(err)
			return
		}
		select {
		case <-ctx.Done():
			return
		case <-tick.C:
		}
	}
}

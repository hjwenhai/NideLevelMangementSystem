package core

import (
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/buildinfo"
	"levelgateway/src/internal/update"
)

func (m *Manager) maintenanceBusy() bool {
	return m.restart.pending.Load() || m.upgrade.BlocksWrites() || m.network.Busy()
}
func (m *Manager) UpgradeStatus() update.Status { return m.upgrade.Status() }
func (m *Manager) RequestUpgrade(id, source string) (update.Status, error) {
	m.applyMu.Lock()
	defer m.applyMu.Unlock()
	if m.upgrade == nil {
		return update.Status{}, fmt.Errorf("当前服务未启用网页升级")
	}
	status := m.upgrade.Status()
	pending := status.Phase == "queued" || status.Phase == "preparing" || status.Phase == "backing_up" || status.Phase == "switching" || status.Phase == "starting" || status.Phase == "verifying" || status.Phase == "rolling_back"
	if status.ID == id && status.Busy && pending {
		return status, nil
	}
	if m.closing.Load() || m.restart.pending.Load() || m.network.Busy() {
		return status, fmt.Errorf("正在退出或重启")
	}
	if !m.ProgramRestartStatus().Supported {
		return status, fmt.Errorf("须按项目service.sh服务方式部署后启用网页升级")
	}
	if m.Engine.HasCycles() || m.Store.Status() != "" {
		return status, fmt.Errorf("存在未复位控制结果或存储异常，请先在原控制方核查并复位；升级不会自动清除控制记录")
	}
	if e := m.upgrade.Begin(id, source); e != nil {
		return m.upgrade.Status(), e
	}
	record, _ := json.Marshal(map[string]string{"id": id, "source": source, "release": status.Target})
	if e := m.Store.Event("upgrade_requested", string(record)); e != nil {
		_ = m.upgrade.Abort(id, "升级审计保存失败，未启动升级")
		return m.upgrade.Status(), e
	}
	child, e := m.upgrade.Launch(id)
	if e != nil {
		_ = m.upgrade.Abort(id, "升级执行器启动失败，原程序未切换")
		return m.upgrade.Status(), e
	}
	go func() { _ = child.Wait() }()
	return m.upgrade.Status(), nil
}
func (m *Manager) upgradeCheck() map[string]any {
	words, e := m.Registers(4, 0, 31)
	return map[string]any{"healthy": m.Healthy(), "idle": !m.Engine.HasCycles() && m.Store.Status() == "", "software": buildinfo.Version, "running_release": update.RunningRelease(m.upgrade.Opts.Base), "job_id": m.UpgradeStatus().ID, "north_ok": m.license.Allowed() && e == nil && len(words) == 31 && words[2] == 3 && words[3] == 64}
}

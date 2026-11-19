package core

import (
	"crypto/subtle"
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/buildinfo"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/fw9000"
	"net/http"
	"strconv"
	"strings"
	"time"
)

func jsonResponse(w http.ResponseWriter, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	_ = json.NewEncoder(w).Encode(v)
}
func (m *Manager) API(token string, clockSet ...bool) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/healthz" && r.Method == "GET" {
			if !m.Healthy() {
				http.Error(w, "采集调度异常", 503)
				return
			}
			jsonResponse(w, map[string]any{"healthy": true, "revision": m.Config().Revision})
			return
		}
		if subtle.ConstantTimeCompare([]byte(r.Header.Get("Authorization")), []byte("Bearer "+token)) != 1 {
			http.Error(w, "未授权", 401)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		switch r.URL.Path {
		case "/api/license/request", "/api/license/import":
			m.licenseAPI(w, r)
		case "/api/network", "/api/network/apply", "/api/network/confirm":
			m.networkAPI(w, r)
		case "/api/maintenance/upgrade/check":
			if r.Method != "GET" || m.upgrade == nil {
				http.Error(w, "升级检查不可用", 404)
				return
			}
			jsonResponse(w, m.upgradeCheck())
		case "/api/maintenance/upgrade":
			if r.Method != "POST" {
				http.Error(w, "方法不支持", 405)
				return
			}
			var req struct {
				ID        string `json:"id"`
				Confirmed bool   `json:"confirmed"`
				Source    string `json:"source"`
			}
			r.Body = http.MaxBytesReader(w, r.Body, 1024)
			dec := json.NewDecoder(r.Body)
			dec.DisallowUnknownFields()
			if dec.Decode(&req) != nil || dec.Decode(&struct{}{}) != io.EOF || !req.Confirmed || !strings.HasPrefix(req.Source, "web:") || len(req.Source) > 128 {
				http.Error(w, "请确认维护窗口及现场无动作", 400)
				return
			}
			status, e := m.RequestUpgrade(req.ID, req.Source)
			if e != nil {
				http.Error(w, e.Error(), 409)
				return
			}
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.WriteHeader(http.StatusAccepted)
			jsonResponse(w, status)
		case "/api/maintenance/restart":
			if r.Method != "POST" {
				http.Error(w, "方法不支持", 405)
				return
			}
			var req struct {
				Confirmed bool   `json:"confirmed"`
				Source    string `json:"source"`
			}
			r.Body = http.MaxBytesReader(w, r.Body, 1024)
			if json.NewDecoder(r.Body).Decode(&req) != nil || !req.Confirmed || !strings.HasPrefix(req.Source, "web:") || len(req.Source) > 128 {
				http.Error(w, "须确认程序重启影响", 400)
				return
			}
			status, err := m.RequestProgramRestart(req.Source)
			if err != nil {
				http.Error(w, err.Error(), 409)
				return
			}
			w.Header().Set("Content-Type", "application/json; charset=utf-8")
			w.WriteHeader(http.StatusAccepted)
			jsonResponse(w, status)
		case "/api/maintenance/password-audit":
			if r.Method != "POST" {
				http.Error(w, "方法不支持", 405)
				return
			}
			var req struct {
				Kind   string `json:"kind"`
				ID     string `json:"id"`
				Source string `json:"source"`
			}
			r.Body = http.MaxBytesReader(w, r.Body, 1024)
			if json.NewDecoder(r.Body).Decode(&req) != nil || len(req.ID) != 32 || len(req.Source) > 128 || !strings.HasPrefix(req.Source, "web:") {
				http.Error(w, "维护审计数据无效", 400)
				return
			}
			if req.Kind != "admin_password_change_prepared" && req.Kind != "admin_password_changed" && req.Kind != "admin_password_change_failed" {
				http.Error(w, "维护事件无效", 400)
				return
			}
			m.applyMu.Lock()
			defer m.applyMu.Unlock()
			if m.maintenanceBusy() || m.closing.Load() {
				http.Error(w, "正在升级或重启，暂不能修改密码", 409)
				return
			}
			data, _ := json.Marshal(map[string]any{"id": req.ID, "source": req.Source})
			if err := m.Store.Event(req.Kind, string(data)); err != nil {
				http.Error(w, "保存维护审计失败", 503)
				return
			}
			jsonResponse(w, map[string]bool{"ok": true})
		case "/api/time":
			if r.Method != "POST" {
				http.Error(w, "方法不支持", 405)
				return
			}
			var req struct {
				UTCMS int64 `json:"utc_ms"`
			}
			r.Body = http.MaxBytesReader(w, r.Body, 1024)
			if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
				http.Error(w, "校时数据无效", 400)
				return
			}
			m.applyMu.Lock()
			defer m.applyMu.Unlock()
			if m.maintenanceBusy() {
				http.Error(w, "正在升级或重启，暂不能校时", 409)
				return
			}
			if err := m.CalibrateClock(req.UTCMS, len(clockSet) > 0 && clockSet[0]); err != nil {
				http.Error(w, err.Error(), 409)
				return
			}
			jsonResponse(w, map[string]any{"ok": true, "clock": m.ClockStatus()})
		case "/api/status":
			if r.Method != "GET" {
				http.Error(w, "方法不支持", 405)
				return
			}
			c := m.Config()
			diag, _ := m.Registers(4, 100, 125)
			w.Header().Set("X-Gateway-Map", "3")
			jsonResponse(w, map[string]any{"version": buildinfo.Version, "license": m.license.Status(), "tcp": c.TCP, "upgrade": m.UpgradeStatus(), "program_restart": m.ProgramRestartStatus(), "map_version": 3, "vendor_docs_version": "V1.8", "control_error": m.Engine.Status(), "boot": fmt.Sprintf("%016x", m.Boot), "config_revision": c.Revision, "mode": c.Mode, "simulate": m.Simulate, "hardware_writes_enabled": m.HardwareWrites, "time_ms": time.Now().UnixMilli(), "time_trusted": m.TimeTrusted.Load(), "clock": m.ClockStatus(), "uptime_sec": int(time.Since(m.Started).Seconds()), "storage_error": m.Store.Status(), "capacity": config.MaxMeters, "max_per_south_port": config.MaxPerPort, "indicators": m.IndicatorStatus(), "ports": diag})
		case "/api/data":
			if r.Method != "GET" {
				http.Error(w, "方法不支持", 405)
				return
			}
			jsonResponse(w, m.Views())
		case "/api/config":
			if r.Method == "GET" {
				jsonResponse(w, m.Config())
				return
			}
			if r.Method != "PUT" {
				http.Error(w, "方法不支持", 405)
				return
			}
			r.Body = http.MaxBytesReader(w, r.Body, 65536)
			raw, e := io.ReadAll(r.Body)
			if e != nil {
				http.Error(w, e.Error(), 400)
				return
			}
			c, e := config.Parse(raw)
			if e == nil {
				e = m.Apply(c)
			}
			if e != nil {
				http.Error(w, e.Error(), 409)
				return
			}
			jsonResponse(w, m.Config())
		case "/api/events":
			if r.Method != "GET" {
				http.Error(w, "方法不支持", 405)
				return
			}
			v, e := m.Store.Events()
			if e != nil {
				http.Error(w, e.Error(), 500)
				return
			}
			jsonResponse(w, v)
		case "/api/commands":
			if r.Method != "GET" {
				http.Error(w, "方法不支持", 405)
				return
			}
			history, e := m.Store.Commands()
			if e != nil {
				http.Error(w, e.Error(), 500)
				return
			}
			jsonResponse(w, map[string]any{"controls": m.Engine.Results(), "control_error": m.Engine.Status(), "recent": history})
		case "/api/control/level-adjust":
			if r.Method != "POST" {
				http.Error(w, "方法不支持", 405)
				return
			}
			var req struct {
				Slot      int    `json:"slot"`
				Target    *int32 `json:"target_raw"`
				Operator  string `json:"operator"`
				Reason    string `json:"reason"`
				Confirmed bool   `json:"site_verified"`
			}
			r.Body = http.MaxBytesReader(w, r.Body, 2048)
			dec := json.NewDecoder(r.Body)
			dec.DisallowUnknownFields()
			if dec.Decode(&req) != nil || !req.Confirmed || req.Target == nil || len(strings.TrimSpace(req.Operator)) < 1 || len(req.Operator) > 128 || len(strings.TrimSpace(req.Reason)) < 4 || len(req.Reason) > 1024 || !command.ValidLevelTarget(*req.Target) {
				http.Error(w, "请填写合法目标液位、核查人员和至少4字原因，并确认现场许可", 400)
				return
			}
			if dec.Decode(&struct{}{}) != io.EOF {
				http.Error(w, "请求数据无效", 400)
				return
			}
			m.applyMu.Lock()
			defer m.applyMu.Unlock()
			if m.maintenanceBusy() || m.closing.Load() {
				http.Error(w, "正在重启或关闭，暂不能修改", 409)
				return
			}
			if err := m.Engine.SubmitLevel(req.Slot, *req.Target, "web/level-adjust", strings.TrimSpace(req.Operator), strings.TrimSpace(req.Reason)); err != nil {
				http.Error(w, err.Error(), 409)
				return
			}
			w.WriteHeader(http.StatusAccepted)
			jsonResponse(w, map[string]any{"accepted": true, "message": "已受理，请查看最终状态；不会自动重写"})
		case "/api/control/maintenance-reset":
			if r.Method != "POST" {
				http.Error(w, "方法不支持", 405)
				return
			}
			var req struct {
				Slot      int    `json:"slot"`
				Stop      bool   `json:"stop"`
				ID        string `json:"id"`
				Operator  string `json:"operator"`
				Reason    string `json:"reason"`
				Confirmed bool   `json:"site_verified"`
			}
			r.Body = http.MaxBytesReader(w, r.Body, 4096)
			if err := json.NewDecoder(r.Body).Decode(&req); err != nil || !req.Confirmed {
				http.Error(w, "须填写核查人员、原因并确认现场已经核查", 400)
				return
			}
			m.applyMu.Lock()
			if m.maintenanceBusy() {
				m.applyMu.Unlock()
				http.Error(w, "正在升级或重启，暂不能复位", 409)
				return
			}
			err := m.Engine.MaintenanceReset(req.Slot, req.Stop, req.ID, strings.TrimSpace(req.Operator), strings.TrimSpace(req.Reason))
			m.applyMu.Unlock()
			if err != nil {
				http.Error(w, err.Error(), 409)
				return
			}
			jsonResponse(w, map[string]bool{"ok": true})
		case "/api/history":
			if r.Method != "GET" {
				http.Error(w, "方法不支持", 405)
				return
			}
			q := r.URL.Query()
			from, _ := strconv.ParseInt(q.Get("from"), 10, 64)
			to, _ := strconv.ParseInt(q.Get("to"), 10, 64)
			after, _ := strconv.ParseInt(q.Get("after"), 10, 64)
			lastSlot, _ := strconv.Atoi(q.Get("after_slot"))
			slot := 0
			if q.Get("slot") != "" {
				var err error
				slot, err = strconv.Atoi(q.Get("slot"))
				if err != nil {
					http.Error(w, "历史槽位无效", 400)
					return
				}
			}
			if to == 0 {
				to = time.Now().UnixMilli()
			}
			if from == 0 {
				from = to - 86400000
			}
			if from < 0 || to < from || to-from > 366*86400000 || slot < 0 || slot > config.MaxMeters {
				http.Error(w, "历史范围无效", 400)
				return
			}
			v, e := m.Store.Query(from, to, after, lastSlot, slot, 2000)
			if e != nil {
				http.Error(w, e.Error(), 500)
				return
			}
			jsonResponse(w, v)
		case "/api/simulator/fault":
			if !m.Simulate || r.Method != "PUT" {
				http.Error(w, "仅模拟运行可用", 404)
				return
			}
			var req struct {
				Port    int          `json:"port"`
				Address byte         `json:"address"`
				Fault   fw9000.Fault `json:"fault"`
			}
			req.Fault.UnsupportedStart = -1
			r.Body = http.MaxBytesReader(w, r.Body, 1024)
			if e := json.NewDecoder(r.Body).Decode(&req); e != nil || req.Fault.DelayMS < 0 || req.Fault.DelayMS > 5000 {
				http.Error(w, "无效故障设置", 400)
				return
			}
			m.applyMu.Lock()
			defer m.applyMu.Unlock()
			if m.maintenanceBusy() {
				http.Error(w, "正在升级或重启", 409)
				return
			}
			m.mu.RLock()
			rt := m.Ports[req.Port]
			if rt == nil || rt.Simulator == nil {
				m.mu.RUnlock()
				http.Error(w, "模拟口不存在", 400)
				return
			}
			e := rt.Simulator.SetFault(req.Address, req.Fault)
			m.mu.RUnlock()
			if e != nil {
				http.Error(w, e.Error(), 400)
				return
			}
			jsonResponse(w, map[string]bool{"ok": true})
		default:
			http.NotFound(w, r)
		}
	})
}
func SafeLocalAddress(address string) bool {
	return strings.HasPrefix(address, "unix:") || strings.HasPrefix(address, "127.0.0.1:") || strings.HasPrefix(address, "[::1]:")
}

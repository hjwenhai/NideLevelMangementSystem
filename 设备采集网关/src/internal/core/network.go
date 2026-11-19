package core

import (
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/network"
	"net/http"
	"strings"
)

func (m *Manager) ConfigureNetwork(s *network.Service, launch func(string) error) {
	m.networkLaunch = launch
}
func (m *Manager) networkAPI(w http.ResponseWriter, r *http.Request) {
	if m.network == nil {
		jsonResponse(w, network.Status{Message: "当前启动方式未启用系统网络设置"})
		return
	}
	if r.Method == "GET" && r.URL.Path == "/api/network" {
		jsonResponse(w, m.network.Status())
		return
	}
	if r.Method != "POST" {
		http.Error(w, "方法不支持", 405)
		return
	}
	var req struct {
		Settings  network.Settings `json:"settings"`
		ID        string           `json:"id"`
		Confirmed bool             `json:"confirmed"`
		Source    string           `json:"source"`
		LocalIP   string           `json:"local_ip"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	dec := json.NewDecoder(r.Body)
	dec.DisallowUnknownFields()
	if dec.Decode(&req) != nil || dec.Decode(&struct{}{}) != io.EOF || !req.Confirmed || !strings.HasPrefix(req.Source, "web:") || len(req.Source) > 128 {
		http.Error(w, "请确认网络修改影响及本次操作", 400)
		return
	}
	m.applyMu.Lock()
	defer m.applyMu.Unlock()
	if r.URL.Path == "/api/network/confirm" {
		status, e := m.network.Confirm(req.ID, req.LocalIP, req.Source)
		if e != nil {
			http.Error(w, e.Error(), 409)
			return
		}
		_ = m.Store.Event("network_confirm_requested", req.Source+" "+req.ID)
		jsonResponse(w, status)
		return
	}
	if r.URL.Path != "/api/network/apply" {
		http.NotFound(w, r)
		return
	}
	if m.maintenanceBusy() || m.closing.Load() || m.Engine.HasCycles() || m.Engine.Status() != "" || m.Store.Status() != "" {
		http.Error(w, "请先结束并人工核查、复位已有控制结果，等待其他维护任务结束", 409)
		return
	}
	if m.networkLaunch == nil {
		http.Error(w, "须通过项目service.sh启动后修改网络", 409)
		return
	}
	status, e := m.network.Begin(req.Settings, req.Source)
	if e != nil {
		http.Error(w, e.Error(), 409)
		return
	}
	audit, _ := json.Marshal(map[string]any{"id": status.ID, "source": req.Source, "settings": req.Settings})
	if e = m.Store.Event("network_change_requested", string(audit)); e == nil {
		e = m.networkLaunch(status.ID)
	}
	if e != nil {
		_ = m.network.AbortUnlaunched(status.ID)
		http.Error(w, fmt.Sprintf("未启动网络修改：%v", e), 503)
		return
	}
	w.WriteHeader(202)
	jsonResponse(w, status)
}

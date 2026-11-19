package core

import (
	"encoding/json"
	"io"
	"levelgateway/src/internal/licensing"
	"net/http"
	"strings"
)

func (m *Manager) licenseAPI(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path == "/api/license/request" {
		if r.Method != "GET" {
			http.Error(w, "方法不支持", 405)
			return
		}
		req, e := m.license.Request()
		if e != nil {
			http.Error(w, e.Error(), 409)
			return
		}
		jsonResponse(w, req)
		return
	}
	if r.Method != "POST" {
		http.Error(w, "方法不支持", 405)
		return
	}
	var req struct {
		License   json.RawMessage `json:"license"`
		Confirmed bool            `json:"confirmed"`
		Source    string          `json:"source"`
	}
	r.Body = http.MaxBytesReader(w, r.Body, licensing.Limit+1024)
	d := json.NewDecoder(r.Body)
	d.DisallowUnknownFields()
	if d.Decode(&req) != nil || d.Decode(&struct{}{}) != io.EOF || !req.Confirmed || !strings.HasPrefix(req.Source, "web:") || len(req.Source) > 128 {
		http.Error(w, "须确认厂家授权文件及来源", 400)
		return
	}
	m.applyMu.Lock()
	defer m.applyMu.Unlock()
	if m.maintenanceBusy() || m.closing.Load() || m.Engine.HasCycles() {
		http.Error(w, "存在维护任务或未复位控制记录，请先核查处理", 409)
		return
	}
	// Prepare audit before replacing the file. Import never activates a running process.
	b, _ := json.Marshal(map[string]string{"source": req.Source})
	if e := m.Store.Event("license_import_requested", string(b)); e != nil {
		http.Error(w, "无法保存授权操作记录", 503)
		return
	}
	if e := m.license.Import(req.License); e != nil {
		http.Error(w, e.Error(), 409)
		return
	}
	s := m.license.Status()
	var saved licensing.Envelope
	_ = licensing.Decode(req.License, &saved) // Import has already verified this exact signed payload.
	record, _ := json.Marshal(map[string]string{"source": req.Source, "license_id": saved.License.ID, "device_code": saved.License.DeviceCode})
	if e := m.Store.Event("license_imported", string(record)); e != nil {
		http.Error(w, "授权已保存，但完成记录写入失败；请检查存储并重新查询状态", 503)
		return
	}
	jsonResponse(w, s)
}

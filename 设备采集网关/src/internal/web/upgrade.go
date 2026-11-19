package web

import (
	"bytes"
	"encoding/json"
	"io"
	"levelgateway/src/internal/buildinfo"
	"levelgateway/src/internal/licensing"
	"levelgateway/src/internal/update"
	"net/http"
	"path/filepath"
)

func (s *Server) ConfigureUpgrade(service *update.Service) {
	s.upgrade = service
	if buildinfo.LicenseBridge && service != nil {
		path := filepath.Join(service.Opts.Base, "config", "license.json")
		s.bridge = &licenseBridge{license: licensing.New(path, true, nil)}
	}
}
func (s *Server) upgradeRequest(w http.ResponseWriter, r *http.Request) {
	if s.upgrade == nil {
		if r.Method == "GET" {
			respond(w, update.Status{Message: "当前启动方式未启用网页升级", UploadLimit: update.UploadLimit})
		} else {
			http.Error(w, "请先由厂家安装支持网页升级的版本并按部署服务启动", 409)
		}
		return
	}
	switch {
	case r.URL.Path == "/api/upgrade/status" && r.Method == "GET":
		respond(w, s.upgrade.Status())
	case r.URL.Path == "/api/upgrade/upload" && r.Method == "POST":
		if r.ContentLength > update.UploadLimit {
			http.Error(w, "升级文件不能超过32MiB", 413)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, update.UploadLimit)
		stage := s.upgrade.Stage
		if s.bridge != nil {
			stage = s.upgrade.StageBridgeTarget
		}
		status, e := stage(r.Context(), r.Body)
		if e != nil {
			http.Error(w, e.Error(), 400)
			return
		}
		respond(w, status)
	case r.URL.Path == "/api/upgrade/confirm" && r.Method == "POST":
		var body struct {
			ID        string `json:"id"`
			Confirmed bool   `json:"confirmed"`
		}
		if decodeMaintenance(w, r, &body) != nil || !body.Confirmed {
			http.Error(w, "请确认维护窗口及现场无动作，升级会中断通讯", 400)
			return
		}
		if s.bridge != nil {
			s.bridge.mu.Lock()
			defer s.bridge.mu.Unlock()
			if e := s.bridge.license.CheckFile(); e != nil {
				http.Error(w, "请先在设备永久授权中校验并保存本机授权："+e.Error(), 409)
				return
			}
		}
		b, _ := json.Marshal(map[string]any{"id": body.ID, "confirmed": true, "source": requestSource(r)})
		response, e := s.request(r.Context(), "POST", "/api/maintenance/upgrade", bytes.NewReader(b))
		if e != nil {
			http.Error(w, "升级请求是否受理尚未确认，请刷新状态，勿重复操作", 503)
			return
		}
		defer response.Body.Close()
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(response.StatusCode)
		_, _ = io.Copy(w, io.LimitReader(response.Body, 16384))
	default:
		http.NotFound(w, r)
	}
}

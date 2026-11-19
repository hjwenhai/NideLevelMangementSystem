package web

import (
	"bytes"
	"encoding/json"
	"io"
	"levelgateway/src/internal/licensing"
	"log"
	"net/http"
	"sync"
)

// Only the migration web server installs a grant locally. The 0.8 core owns
// licensing in the regular application and verifies this file again on boot.
type licenseBridge struct {
	license *licensing.Controller
	mu      sync.Mutex
}

func (b *licenseBridge) status() licensing.Status {
	s := licensing.Status{Required: true, State: "bridge"}
	if g, err := b.license.SavedGrant(); err == nil {
		s.FileValid, s.Valid, s.Permanent = true, true, true
		s.ID, s.Kind, s.DeviceCode, s.Customer = g.ID, g.Kind, g.DeviceCode, g.Customer
		s.Message = "本机永久授权校验通过，可在网页升级中安装 0.8.1。"
	} else {
		s.Message = err.Error()
		if req, err := b.license.Request(); err == nil {
			s.Kind, s.DeviceCode = req.Kind, req.DeviceCode
		} else {
			s.Message = err.Error()
		}
	}
	return s
}

// Authentication, same-origin and CSRF checks run in Server.Handler first.
func (s *Server) licenseRequest(w http.ResponseWriter, r *http.Request) {
	var body io.Reader
	switch {
	case r.URL.Path == "/api/license/request" && r.Method == "GET":
		if s.bridge != nil {
			req, err := s.bridge.license.Request()
			if err != nil {
				http.Error(w, err.Error(), 409)
				return
			}
			respond(w, req)
			return
		}
	case r.URL.Path == "/api/license/import" && r.Method == "POST":
		var req struct {
			License   json.RawMessage `json:"license"`
			Confirmed bool            `json:"confirmed"`
		}
		r.Body = http.MaxBytesReader(w, r.Body, licensing.Limit+1024)
		d := json.NewDecoder(r.Body)
		d.DisallowUnknownFields()
		if d.Decode(&req) != nil || d.Decode(&struct{}{}) != io.EOF || !req.Confirmed || len(req.License) > licensing.Limit {
			http.Error(w, "授权文件无效或未确认", 400)
			return
		}
		if s.bridge != nil {
			s.bridge.mu.Lock()
			defer s.bridge.mu.Unlock()
			if s.upgrade.BlocksWrites() {
				http.Error(w, "正在升级，请等待完成后再导入", 409)
				return
			}
			if err := s.bridge.license.Import(req.License); err != nil {
				http.Error(w, err.Error(), 409)
				return
			}
			status := s.bridge.status()
			log.Printf("已保存本机升级授权 id=%s source=%s", status.ID, requestSource(r))
			respond(w, status)
			return
		}
		b, _ := json.Marshal(map[string]any{"license": req.License, "confirmed": true, "source": requestSource(r)})
		body = bytes.NewReader(b)
	default:
		http.NotFound(w, r)
		return
	}
	resp, e := s.request(r.Context(), r.Method, r.URL.Path, body)
	if e != nil {
		http.Error(w, "授权请求暂不可用，请查询状态", 503)
		return
	}
	defer resp.Body.Close()
	w.Header().Set("Content-Type", resp.Header.Get("Content-Type"))
	w.WriteHeader(resp.StatusCode)
	_, _ = io.Copy(w, io.LimitReader(resp.Body, 16384))
}

package web

import (
	"bytes"
	"encoding/json"
	"io"
	"levelgateway/src/internal/network"
	"net"
	"net/http"
)

func (s *Server) networkRequest(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/api/network" && r.URL.Path != "/api/network/apply" && r.URL.Path != "/api/network/confirm" {
		http.NotFound(w, r)
		return
	}
	var body io.Reader
	if r.Method == "POST" {
		var req struct {
			Settings  network.Settings `json:"settings"`
			ID        string           `json:"id"`
			Confirmed bool             `json:"confirmed"`
		}
		if decodeMaintenance(w, r, &req) != nil || !req.Confirmed {
			http.Error(w, "请确认网络修改影响", 400)
			return
		}
		local := ""
		if addr, ok := r.Context().Value(http.LocalAddrContextKey).(net.Addr); ok {
			local, _, _ = net.SplitHostPort(addr.String())
		}
		// The destination address comes from the accepted socket. Never trust Host,
		// Forwarded headers or an address supplied by the browser as proof of access.
		b, _ := json.Marshal(map[string]any{"settings": req.Settings, "id": req.ID, "confirmed": req.Confirmed, "source": requestSource(r), "local_ip": local})
		body = bytes.NewReader(b)
	} else if r.Method != "GET" || r.URL.Path != "/api/network" {
		http.Error(w, "方法不支持", 405)
		return
	}
	resp, e := s.request(r.Context(), r.Method, r.URL.Path, body)
	if e != nil {
		http.Error(w, "网络维护请求结果未确认，请查询状态，不要重复提交", 503)
		return
	}
	defer resp.Body.Close()
	w.Header().Set("Content-Type", resp.Header.Get("Content-Type"))
	w.WriteHeader(resp.StatusCode)
	_, _ = io.Copy(w, io.LimitReader(resp.Body, 65536))
}

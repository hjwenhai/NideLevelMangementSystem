package web

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestNetworkAuthAndAcceptedSocketProof(t *testing.T) {
	f := maintenanceFixture(t)
	cookie, csrf := f.login("Old-Password-2026!")
	for _, tc := range []struct {
		cookie *http.Cookie
		csrf   string
		body   string
		want   int
	}{
		{nil, "", `{"confirmed":true}`, 401}, {cookie, "bad", `{"confirmed":true}`, 403}, {cookie, csrf, `{"confirmed":false}`, 400},
		{cookie, csrf, `{"confirmed":true,"local_ip":"192.168.1.231"}`, 400},
	} {
		w := f.call("POST", "/api/network/confirm", tc.body, tc.cookie, tc.csrf)
		if w.Code != tc.want {
			t.Fatal(w.Code, tc.want, w.Body.String())
		}
	}
	req := httptest.NewRequest("POST", "http://192.168.1.230/api/network/confirm", strings.NewReader(`{"confirmed":true,"id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}`))
	req.RemoteAddr = "192.168.1.100:1234"
	req.AddCookie(cookie)
	req.Header.Set("X-CSRF-Token", csrf)
	req.Header.Set("X-Forwarded-Host", "192.168.1.231")
	req = req.WithContext(context.WithValue(req.Context(), http.LocalAddrContextKey, &net.TCPAddr{IP: net.ParseIP("192.168.1.232"), Port: 8088}))
	w := httptest.NewRecorder()
	f.s.Handler().ServeHTTP(w, req)
	if w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	var forwarded map[string]any
	_ = json.Unmarshal([]byte(f.audit[len(f.audit)-1]), &forwarded)
	if forwarded["local_ip"] != "192.168.1.232" || forwarded["source"] != "web:192.168.1.100" {
		t.Fatal(forwarded)
	}
	if w = f.call("POST", "/api/network/apply", `{"confirmed":true} {}`, cookie, csrf); w.Code != 400 {
		t.Fatal("multiple JSON objects accepted")
	}
	if w = f.call("GET", "/api/network/unknown", "", cookie, csrf); w.Code != 404 {
		t.Fatal("unknown route")
	}
}

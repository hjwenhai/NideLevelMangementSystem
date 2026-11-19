package web

import (
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestLoginCSRFOriginAndProxy(t *testing.T) {
	d := t.TempDir()
	password := "Testing-Only-Strong-123!"
	auth := filepath.Join(d, "admin")
	token := filepath.Join(d, "token")
	if e := InitAdmin(auth, password); e != nil {
		t.Fatal(e)
	}
	if InitAdmin(auth, password) == nil {
		t.Fatal("overwrote admin")
	}
	os.WriteFile(token, []byte("internal"), 0600)
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer internal" {
			t.Error("missing internal auth")
		}
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/config" && r.Method == "GET" {
			json.NewEncoder(w).Encode(config.Default(1))
			return
		}
		io.WriteString(w, "[]")
	}))
	defer upstream.Close()
	s, e := New(strings.TrimPrefix(upstream.URL, "http://"), token, auth)
	if e != nil {
		t.Fatal(e)
	}
	h := s.Handler()
	invoke := func(method, path, body string, cookie *http.Cookie, csrf, origin string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(method, path, strings.NewReader(body))
		r.RemoteAddr = "127.0.0.1:12345"
		if cookie != nil {
			r.AddCookie(cookie)
		}
		r.Header.Set("X-CSRF-Token", csrf)
		r.Header.Set("Origin", origin)
		w := httptest.NewRecorder()
		h.ServeHTTP(w, r)
		return w
	}
	if invoke("GET", "/api/data", "", nil, "", "").Code != 401 {
		t.Fatal("unauthenticated data")
	}
	login := invoke("POST", "/api/login", "{\"password\":\""+password+"\"}", nil, "", "")
	if login.Code != 200 {
		t.Fatal(login.Body.String())
	}
	cookie := login.Result().Cookies()[0]
	if !cookie.HttpOnly || cookie.SameSite != http.SameSiteStrictMode {
		t.Fatal("cookie")
	}
	var session map[string]string
	json.Unmarshal(login.Body.Bytes(), &session)
	if invoke("PUT", "/api/config", "{}", cookie, "", "").Code != 403 {
		t.Fatal("missing CSRF")
	}
	if invoke("PUT", "/api/config", "{}", cookie, session["csrf"], "https://foreign.test").Code != 403 {
		t.Fatal("foreign origin")
	}
	if invoke("PUT", "/api/config", "{}", cookie, session["csrf"], "").Code != 200 {
		t.Fatal("valid config proxy")
	}
	if invoke("POST", "/api/control/maintenance-reset", "{}", cookie, "", "").Code != 403 {
		t.Fatal("reset bypassed CSRF")
	}
	if invoke("POST", "/api/control/maintenance-reset", "{}", cookie, session["csrf"], "").Code != 200 {
		t.Fatal("maintenance proxy unavailable")
	}
	if invoke("GET", "/api/simulator/fault", "", cookie, "", "").Code != 404 {
		t.Fatal("unexpected endpoint")
	}
	now := time.Now().UnixMilli()
	w := invoke("GET", fmt.Sprintf("/api/history.csv?from=%d&to=%d", now-1000, now), "", cookie, "", "")
	if w.Code != 200 || !strings.Contains(w.Header().Get("Content-Type"), "text/csv") || !strings.Contains(w.Body.String(), "绑定版本") {
		t.Fatal("csv", w.Code)
	}
	if invoke("POST", "/api/logout", "", cookie, session["csrf"], "").Code != 200 {
		t.Fatal("logout")
	}
	if invoke("GET", "/api/data", "", cookie, "", "").Code != 401 {
		t.Fatal("session remains")
	}
	if invoke("GET", "/app.js", "", nil, "", "").Code != 200 {
		t.Fatal("offline asset")
	}
}

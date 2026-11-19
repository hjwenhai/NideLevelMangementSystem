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
)

type maintenanceWeb struct {
	s           *Server
	call        func(string, string, string, *http.Cookie, string) *httptest.ResponseRecorder
	login       func(string) (*http.Cookie, string)
	auth        string
	audit       []string
	unavailable bool
}

func maintenanceFixture(t *testing.T) *maintenanceWeb {
	t.Helper()
	f := &maintenanceWeb{}
	dir := t.TempDir()
	f.auth = filepath.Join(dir, "admin.json")
	token := filepath.Join(dir, "token")
	os.WriteFile(token, []byte("local"), 0600)
	if err := InitAdmin(f.auth, "Old-Password-2026!"); err != nil {
		t.Fatal(err)
	}
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer local" {
			t.Error("missing internal token")
		}
		data, _ := io.ReadAll(r.Body)
		f.audit = append(f.audit, string(data))
		if f.unavailable {
			http.Error(w, "audit unavailable", 503)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"ok":true}`)
	}))
	t.Cleanup(upstream.Close)
	var err error
	f.s, err = New(strings.TrimPrefix(upstream.URL, "http://"), token, f.auth)
	if err != nil {
		t.Fatal(err)
	}
	handler := f.s.Handler()
	f.call = func(method, path, body string, cookie *http.Cookie, csrf string) *httptest.ResponseRecorder {
		req := httptest.NewRequest(method, path, strings.NewReader(body))
		req.RemoteAddr = "127.0.0.1:12345"
		if cookie != nil {
			req.AddCookie(cookie)
		}
		req.Header.Set("X-CSRF-Token", csrf)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, req)
		return w
	}
	f.login = func(password string) (*http.Cookie, string) {
		body, _ := json.Marshal(map[string]string{"password": password})
		w := f.call("POST", "/api/login", string(body), nil, "")
		if w.Code != 200 {
			t.Fatalf("login: %d %s", w.Code, w.Body)
		}
		var response map[string]string
		json.Unmarshal(w.Body.Bytes(), &response)
		return w.Result().Cookies()[0], response["csrf"]
	}
	return f
}
func passwordBody(old, next, confirm string) string {
	b, _ := json.Marshal(map[string]string{"current_password": old, "new_password": next, "confirm_password": confirm})
	return string(b)
}
func TestPasswordChangeRevokesAllSessionsAndPersists(t *testing.T) {
	f := maintenanceFixture(t)
	c, csrf := f.login("Old-Password-2026!")
	other, otherCSRF := f.login("Old-Password-2026!")
	before, _ := os.ReadFile(f.auth)
	for _, b := range []string{passwordBody("wrong", "New-Password-2026!", "New-Password-2026!"), passwordBody("Old-Password-2026!", "tiny", "tiny"), passwordBody("Old-Password-2026!", "New-Password-2026!", "different"), passwordBody("Old-Password-2026!", "含中文密码abcdefghijkl", "含中文密码abcdefghijkl"), passwordBody("Old-Password-2026!", "Old-Password-2026!", "Old-Password-2026!")} {
		if w := f.call("POST", "/api/admin/password", b, c, csrf); w.Code != 400 {
			t.Fatal("invalid password accepted", w.Code, w.Body)
		}
	}
	after, _ := os.ReadFile(f.auth)
	if string(before) != string(after) {
		t.Fatal("invalid request changed credentials")
	}
	body := passwordBody("Old-Password-2026!", "New-Password-2026!", "New-Password-2026!")
	if f.call("POST", "/api/admin/password", body, c, "").Code != 403 {
		t.Fatal("CSRF bypass")
	}
	w := f.call("POST", "/api/admin/password", body, c, csrf)
	if w.Code != 200 {
		t.Fatal(w.Code, w.Body)
	}
	if f.call("GET", "/api/session", "", other, otherCSRF).Code != 401 || f.call("GET", "/api/session", "", c, csrf).Code != 401 {
		t.Fatal("old sessions remain")
	}
	c, _ = f.login("New-Password-2026!")
	var persisted Credential
	data, _ := os.ReadFile(f.auth)
	json.Unmarshal(data, &persisted)
	if !credentialMatches(persisted, "New-Password-2026!") || credentialMatches(persisted, "Old-Password-2026!") {
		t.Fatal("persistent identity mismatch")
	}
	if strings.Contains(strings.Join(f.audit, ""), "Password") || !strings.Contains(strings.Join(f.audit, ""), "admin_password_changed") {
		t.Fatal("password leaked to audit or success missing", f.audit)
	}
	if f.call("GET", "/api/admin/password", "", c, "").Code != 405 {
		t.Fatal("GET allowed to mutate")
	}
}
func TestPasswordFailuresAndCommittedWarning(t *testing.T) {
	for _, mode := range []string{"audit", "write", "committed"} {
		t.Run(mode, func(t *testing.T) {
			f := maintenanceFixture(t)
			c, csrf := f.login("Old-Password-2026!")
			old, _ := os.ReadFile(f.auth)
			if mode == "audit" {
				f.unavailable = true
			}
			if mode == "write" {
				f.s.writeCredential = func(string, []byte) error { return fmt.Errorf("disk unavailable") }
			}
			if mode == "committed" {
				f.s.writeCredential = func(p string, b []byte) error {
					if e := config.WriteAtomic(p, b); e != nil {
						return e
					}
					return &config.CommitError{Err: fmt.Errorf("dir sync failed")}
				}
			}
			w := f.call("POST", "/api/admin/password", passwordBody("Old-Password-2026!", "New-Password-2026!", "New-Password-2026!"), c, csrf)
			data, _ := os.ReadFile(f.auth)
			if mode == "committed" {
				if w.Code != 200 || !strings.Contains(w.Body.String(), "已生效") || !credentialMatches(f.s.credential, "New-Password-2026!") {
					t.Fatal(w.Code, w.Body)
				}
			} else {
				if w.Code < 500 || string(data) != string(old) || f.call("GET", "/api/session", "", c, "").Code != 200 {
					t.Fatal("failed change did not retain old identity", w.Code)
				}
			}
		})
	}
}
func TestPasswordChangeRateLimitAndRestartConfirmation(t *testing.T) {
	f := maintenanceFixture(t)
	c, csrf := f.login("Old-Password-2026!")
	for i := 0; i < 5; i++ {
		if w := f.call("POST", "/api/admin/password", passwordBody("wrong", "New-Password-2026!", "New-Password-2026!"), c, csrf); w.Code != 400 {
			t.Fatal(w.Code)
		}
	}
	if f.call("POST", "/api/admin/password", passwordBody("wrong", "New-Password-2026!", "New-Password-2026!"), c, csrf).Code != 429 {
		t.Fatal("old password guesses unlimited")
	}
	if f.call("POST", "/api/maintenance/restart", `{"confirmed":false}`, c, csrf).Code != 400 {
		t.Fatal("restart without confirmation")
	}
	if f.call("POST", "/api/maintenance/restart", `{"confirmed":true,"script":"/tmp/bad"}`, c, csrf).Code != 400 {
		t.Fatal("untrusted script accepted")
	}
	if f.call("POST", "/api/maintenance/restart", `{"confirmed":true}`, c, "").Code != 403 {
		t.Fatal("restart without CSRF")
	}
	if w := f.call("POST", "/api/maintenance/restart", `{"confirmed":true}`, c, csrf); w.Code != 200 || !strings.Contains(f.audit[len(f.audit)-1], "web:127.0.0.1") {
		t.Fatal("restart source not server derived", w.Code)
	}
}

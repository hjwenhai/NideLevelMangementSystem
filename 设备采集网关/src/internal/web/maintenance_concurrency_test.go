package web

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestPasswordRevocationSerializesWithAuthorizedWrites(t *testing.T) {
	dir := t.TempDir()
	auth, token := filepath.Join(dir, "admin"), filepath.Join(dir, "token")
	if err := InitAdmin(auth, "Old-Password-2026!"); err != nil {
		t.Fatal(err)
	}
	os.WriteFile(token, []byte("internal"), 0600)
	entered, release := make(chan struct{}), make(chan struct{})
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/api/config" {
			close(entered)
			<-release
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"ok":true}`)
	}))
	defer upstream.Close()
	s, err := New(strings.TrimPrefix(upstream.URL, "http://"), token, auth)
	if err != nil {
		t.Fatal(err)
	}
	h := s.Handler()
	invoke := func(path, body, method string, cookie *http.Cookie, csrf string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(method, path, strings.NewReader(body))
		if cookie != nil {
			r.AddCookie(cookie)
		}
		r.Header.Set("X-CSRF-Token", csrf)
		w := httptest.NewRecorder()
		h.ServeHTTP(w, r)
		return w
	}
	login := invoke("/api/login", `{"password":"Old-Password-2026!"}`, "POST", nil, "")
	var response map[string]string
	json.Unmarshal(login.Body.Bytes(), &response)
	cookie, csrf := login.Result().Cookies()[0], response["csrf"]
	writeDone := make(chan int, 1)
	go func() { writeDone <- invoke("/api/config", `{}`, "PUT", cookie, csrf).Code }()
	<-entered
	changeDone := make(chan int, 1)
	go func() {
		changeDone <- invoke("/api/admin/password", passwordBody("Old-Password-2026!", "New-Password-2026!", "New-Password-2026!"), "POST", cookie, csrf).Code
	}()
	select {
	case <-changeDone:
		close(release)
		t.Fatal("credential changed before previously authorized request completed")
	case <-time.After(30 * time.Millisecond):
	}
	close(release)
	if <-writeDone != 200 || <-changeDone != 200 {
		t.Fatal("serialized requests failed")
	}
	if invoke("/api/config", `{}`, "PUT", cookie, csrf).Code != 401 {
		t.Fatal("revoked session was allowed to write")
	}
}

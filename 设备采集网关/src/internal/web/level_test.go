package web

import (
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

type levelTransport func(*http.Request) (*http.Response, error)

func (f levelTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
func TestRemoteLevelWebSessionOriginCSRFAndProxyWithoutNetwork(t *testing.T) {
	d := t.TempDir()
	token := filepath.Join(d, "token")
	if err := os.WriteFile(token, []byte("internal-test"), 0600); err != nil {
		t.Fatal(err)
	}
	calls := 0
	s := &Server{base: "http://core.invalid", tokenFile: token, sessions: map[string]session{"test-session": {CSRF: "csrf-test", Expires: time.Now().Add(time.Hour)}}, limits: map[string]limiter{}, export: make(chan struct{}, 1)}
	s.client = &http.Client{Transport: levelTransport(func(r *http.Request) (*http.Response, error) {
		calls++
		if r.Header.Get("Authorization") != "Bearer internal-test" || r.URL.Path != "/api/control/level-adjust" || r.Method != "POST" {
			t.Error("proxy authorization/route")
		}
		body, _ := io.ReadAll(r.Body)
		if !strings.Contains(string(body), `"target_raw":0`) {
			t.Error("target body lost")
		}
		return &http.Response{StatusCode: 202, Header: http.Header{"Content-Type": []string{"application/json"}}, Body: io.NopCloser(strings.NewReader(`{"accepted":true}`))}, nil
	})}
	invoke := func(cookie, csrf, origin string) *httptest.ResponseRecorder {
		r := httptest.NewRequest("POST", "http://gateway.invalid/api/control/level-adjust", strings.NewReader(`{"slot":1,"target_raw":0,"site_verified":true}`))
		if cookie != "" {
			r.AddCookie(&http.Cookie{Name: "gateway_session", Value: cookie})
		}
		r.Header.Set("X-CSRF-Token", csrf)
		r.Header.Set("Origin", origin)
		w := httptest.NewRecorder()
		s.Handler().ServeHTTP(w, r)
		return w
	}
	if invoke("", "csrf-test", "").Code != 401 || invoke("test-session", "", "").Code != 403 || invoke("test-session", "csrf-test", "http://foreign.invalid").Code != 403 {
		t.Fatal("new route bypassed a guard")
	}
	if calls != 0 {
		t.Fatal("guard failure reached core")
	}
	if w := invoke("test-session", "csrf-test", "http://gateway.invalid"); w.Code != 202 || calls != 1 {
		t.Fatal(w.Code, w.Body.String(), calls)
	}
}

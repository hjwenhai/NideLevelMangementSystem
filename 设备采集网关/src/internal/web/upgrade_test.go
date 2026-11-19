package web

import (
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestUpgradeRoutesRequireSessionOriginAndCSRF(t *testing.T) {
	s := &Server{sessions: map[string]session{"test": {CSRF: "csrf", Expires: time.Now().Add(time.Hour)}}, limits: map[string]limiter{}}
	for _, path := range []string{"/api/upgrade/upload", "/api/upgrade/confirm"} {
		for _, tc := range []struct {
			cookie, csrf, origin string
			code                 int
		}{{"", "csrf", "", 401}, {"test", "", "", 403}, {"test", "csrf", "http://foreign.invalid", 403}, {"test", "csrf", "", 409}} {
			r := httptest.NewRequest("POST", "http://gateway.invalid"+path, strings.NewReader("payload"))
			if tc.cookie != "" {
				r.AddCookie(&http.Cookie{Name: "gateway_session", Value: tc.cookie})
			}
			r.Header.Set("X-CSRF-Token", tc.csrf)
			r.Header.Set("Origin", tc.origin)
			w := httptest.NewRecorder()
			s.Handler().ServeHTTP(w, r)
			if w.Code != tc.code {
				t.Fatal(path, w.Code, w.Body.String())
			}
		}
	}
}

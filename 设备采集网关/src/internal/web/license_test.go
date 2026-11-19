package web

import (
	"strings"
	"testing"
)

func TestLicenseAuthCSRFAndServerSuppliedSource(t *testing.T) {
	f := maintenanceFixture(t)
	body := `{"license":{},"confirmed":true}`
	if f.call("GET", "/api/license/request", "", nil, "").Code != 401 {
		t.Fatal("request exposed without login")
	}
	c, csrf := f.login("Old-Password-2026!")
	if f.call("POST", "/api/license/import", body, c, "").Code != 403 {
		t.Fatal("CSRF bypass")
	}
	if f.call("POST", "/api/license/import", `{"license":{},"confirmed":false}`, c, csrf).Code != 400 {
		t.Fatal("missing confirmation")
	}
	if f.call("POST", "/api/license/import", `{"license":{},"confirmed":true,"source":"web:spoof"}`, c, csrf).Code != 400 {
		t.Fatal("spoofed source")
	}
	if f.call("POST", "/api/license/import", body, c, csrf).Code != 200 {
		t.Fatal("valid import route")
	}
	if len(f.audit) == 0 || !strings.Contains(f.audit[len(f.audit)-1], `"source":"web:127.0.0.1"`) {
		t.Fatal("no server supplied audit", f.audit)
	}
}

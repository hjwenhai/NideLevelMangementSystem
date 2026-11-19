package core

import (
	"bytes"
	"encoding/json"
	"levelgateway/src/internal/buildinfo"
	"levelgateway/src/internal/update"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestUpgradeDurableGateBlocksEveryMutation(t *testing.T) {
	m := testManager(t, 1)
	base := t.TempDir()
	s, e := update.New(update.Options{Base: base})
	if e != nil {
		t.Fatal(e)
	}
	m.upgrade = s
	state := filepath.Join(base, "data/upgrades/state.json")
	os.MkdirAll(filepath.Dir(state), 0700)
	os.WriteFile(state, []byte(`{"id":"01234567890123456789012345678901","phase":"needs_attention","manifest":{},"previous":"","source":"web:test","message":"中断","updated_ms":1,"boot_id":"old","pid":0}`), 0600)
	if !m.maintenanceBusy() {
		t.Fatal("startup gate missing")
	}
	if p := m.Handle("tcp:test", 1, fc06(30000, 5)); !bytes.Equal(p, []byte{0x86, 6}) {
		t.Fatal(p)
	}
	if p := m.Handle("tcp:test", 1, fc06(30000, 0)); !bytes.Equal(p, []byte{0x86, 6}) {
		t.Fatal(p)
	}
	if e = m.Apply(m.Config()); e == nil {
		t.Fatal("config changed")
	}
	m.ConfigureProgramRestart(func() (func() error, error) { t.Fatal("restart launched"); return nil, nil })
	if _, e = m.RequestProgramRestart("web:test"); e == nil {
		t.Fatal("restart allowed")
	}
	for _, tc := range []struct{ path, method, body string }{{"/api/time", "POST", `{"utc_ms":1793095842761}`}, {"/api/maintenance/password-audit", "POST", `{"kind":"admin_password_change_prepared","id":"01234567890123456789012345678901","source":"web:test"}`}, {"/api/control/level-adjust", "POST", `{"slot":1,"target_raw":100,"operator":"test","reason":"现场确认","site_verified":true}`}, {"/api/control/maintenance-reset", "POST", `{"slot":1,"stop":false,"id":"test","operator":"test","reason":"现场确认","site_verified":true}`}, {"/api/simulator/fault", "PUT", `{"port":1,"address":1,"fault":{"unsupported_start":-1}}`}} {
		t.Run(tc.path, func(t *testing.T) {
			r := httptest.NewRequest(tc.method, tc.path, strings.NewReader(tc.body))
			r.Header.Set("Authorization", "Bearer test")
			w := httptest.NewRecorder()
			m.API("test", true).ServeHTTP(w, r)
			if w.Code != 409 {
				t.Fatalf("%d %s", w.Code, w.Body.String())
			}
		})
	}
	r := httptest.NewRequest("GET", "/api/status", nil)
	r.Header.Set("Authorization", "Bearer test")
	w := httptest.NewRecorder()
	m.API("test", false).ServeHTTP(w, r)
	var out map[string]any
	json.Unmarshal(w.Body.Bytes(), &out)
	if w.Code != 200 || out["version"] != buildinfo.Version {
		t.Fatal(w.Code, w.Body.String())
	}
	if m.Ports[1].Simulator.Count(1) != 0 {
		t.Fatal("action sent during upgrade")
	}
	os.Remove(state)
	if m.maintenanceBusy() {
		t.Fatal("gate did not release after terminal cleanup")
	}
}

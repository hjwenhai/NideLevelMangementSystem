package core

import (
	"encoding/json"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/network"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestPendingNetworkBlocksAllControlAndMaintenance(t *testing.T) {
	m := restartFixture(t)
	base := t.TempDir()
	_ = os.MkdirAll(filepath.Join(base, "data"), 0700)
	b, _ := json.Marshal(map[string]any{"id": strings.Repeat("a", 32), "phase": "trial"})
	if e := config.WriteAtomic(filepath.Join(base, "data", "network-state.json"), b); e != nil {
		t.Fatal(e)
	}
	m.network = &network.Service{Opts: network.Options{Base: base}}
	if !m.maintenanceBusy() {
		t.Fatal("network gate missing")
	}
	if e := m.Apply(m.Config()); e == nil {
		t.Fatal("config accepted")
	}
	if _, e := m.RequestProgramRestart("web:test"); e == nil {
		t.Fatal("restart accepted")
	}
	for _, addr := range []uint16{30000, 30007, 31000} {
		out := m.Handle("tcp:test", 1, []byte{6, byte(addr >> 8), byte(addr), 0, 0})
		if len(out) != 2 || out[1] != 6 {
			t.Fatalf("write %d not busy: %v", addr, out)
		}
	}
	for _, p := range []string{"/api/time", "/api/control/maintenance-reset", "/api/maintenance/password-audit"} {
		body := `{"utc_ms":1794000000000,"site_verified":true}`
		if strings.Contains(p, "password") {
			body = `{"kind":"admin_password_change_prepared","id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","source":"web:test"}`
		}
		r := httptest.NewRequest("POST", p, strings.NewReader(body))
		r.Header.Set("Authorization", "Bearer t")
		w := httptest.NewRecorder()
		m.API("t", true).ServeHTTP(w, r)
		if w.Code != 409 {
			t.Fatal(p, w.Code, w.Body.String())
		}
	}
}

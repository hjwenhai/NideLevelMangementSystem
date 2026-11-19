package core

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/licensing"
	"levelgateway/src/internal/storage"
	"levelgateway/src/internal/update"
	"net"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestUnlicensedRuntimeAndActivation(t *testing.T) {
	dir := t.TempDir()
	cfg := config.Default(1)
	// Physical mode: any accidental serial open would fail on this PC.
	cfg.History.PeriodSec = 1
	l, e := net.Listen("tcp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	cfg.TCP.Listen = l.Addr().String()
	l.Close()
	s, e := storage.Open(filepath.Join(dir, "db"))
	if e != nil {
		t.Fatal(e)
	}
	defer s.Close()
	pub, key, _ := ed25519.GenerateKey(rand.Reader)
	keys := licensing.PublicKeys{Keys: map[string]string{licensing.KeyID(pub): hex.EncodeToString(pub)}}
	reader := func(string) (string, error) { return "0102030405060708", nil }
	path := filepath.Join(dir, "license.json")
	license := licensing.NewWithKeys(path, true, reader, keys)
	m, e := NewWithLicense(context.Background(), cfg, filepath.Join(dir, "config.json"), s, false, true, nil, nil, license)
	if e != nil {
		t.Fatal("unlicensed must not open physical serial", e)
	}
	defer m.Close()
	if m.tcp != nil || m.Ports[1].Wire != nil || m.Ports[1].Port.Role != config.Disabled {
		t.Fatal("unlicensed runtime accessed hardware")
	}
	if p := m.Handle("tcp:test", 1, []byte{4, 0, 0, 0, 1}); len(p) != 2 || p[0] != 0x84 || p[1] != 6 {
		t.Fatal("unlicensed Modbus exposed data", p)
	}
	if _, e := m.freezeCommand(1, 6, false, "test"); e == nil {
		t.Fatal("unlicensed control allowed")
	}
	m.upgrade = &update.Service{Opts: update.Options{Base: dir}}
	if m.upgradeCheck()["north_ok"] != false {
		t.Fatal("upgrade must not accept an unlicensed target")
	}
	m.upgrade = nil
	api := m.API("token")
	invoke := func(method, url, body string) *httptest.ResponseRecorder {
		r := httptest.NewRequest(method, url, strings.NewReader(body))
		r.Header.Set("Authorization", "Bearer token")
		w := httptest.NewRecorder()
		api.ServeHTTP(w, r)
		return w
	}
	if invoke("GET", "/api/status", "").Code != 200 {
		t.Fatal("management must remain available")
	}
	w := invoke("GET", "/api/license/request", "")
	var req licensing.Request
	if e := json.Unmarshal(w.Body.Bytes(), &req); e != nil {
		t.Fatal(e)
	}
	env, _ := licensing.NewGrant(req, "测试客户", key)
	body, _ := json.Marshal(map[string]any{"license": env, "confirmed": true, "source": "web:127.0.0.1"})
	w = invoke("POST", "/api/license/import", string(body))
	if w.Code != 200 {
		t.Fatal(w.Code, w.Body.String())
	}
	if license.Allowed() || !license.Status().RestartRequired || m.tcp != nil {
		t.Fatal("import must not hot-start hardware")
	}
	// No fake history is saved while waiting for activation.
	time.Sleep(1100 * time.Millisecond)
	rows, e := s.Query(1, time.Now().UnixMilli()+1000, 0, 0, 0, 100)
	if e != nil || len(rows) != 0 {
		t.Fatal("unlicensed history", rows, e)
	}
	active := licensing.NewWithKeys(path, true, reader, keys)
	if !active.Allowed() {
		t.Fatal("restart failed to activate")
	}
	cfg.TCP.Enabled = false
	m2, e := NewWithLicense(context.Background(), cfg, filepath.Join(dir, "config.json"), s, true, false, nil, nil, active)
	if e != nil {
		t.Fatal(e)
	}
	defer m2.Close()
	until(t, func() bool { return m2.Views()[0].Valid == 127 })
}

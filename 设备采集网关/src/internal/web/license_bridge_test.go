//go:build licensebridge

package web

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"levelgateway/src/internal/licensing"
	"levelgateway/src/internal/update"
	"os"
	"path/filepath"
	"testing"
)

func TestBridgeLicenseHandoff(t *testing.T) {
	f := maintenanceFixture(t)
	dir := t.TempDir()
	f.s.ConfigureUpgrade(&update.Service{Opts: update.Options{Base: dir}})
	path := filepath.Join(dir, "config", "license.json")
	if err := os.Mkdir(filepath.Dir(path), 0700); err != nil {
		t.Fatal(err)
	}
	pub, key, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	keys := licensing.PublicKeys{Keys: map[string]string{licensing.KeyID(pub): hex.EncodeToString(pub)}}
	reader := func(string) (string, error) { return "1234567812345678", nil }
	if f.s.bridge == nil || f.s.bridge.license.Path != path {
		t.Fatal("wrong migration location")
	}
	f.s.bridge.license = licensing.NewWithKeys(path, true, reader, keys)
	c, csrf := f.login("Old-Password-2026!")
	post := func(path, body string) int { return f.call("POST", path, body, c, csrf).Code }
	confirm := `{"id":"job","confirmed":true}`
	if post("/api/upgrade/confirm", confirm) != 409 {
		t.Fatal("upgrade without grant")
	}
	if f.call("GET", "/api/license/request", "", nil, "").Code != 401 {
		t.Fatal("anonymous request")
	}
	status := f.call("GET", "/api/status", "", c, csrf)
	if status.Header().Get("X-Gateway-Web-Version") != "0.7.0" {
		t.Fatal("old updater probe incompatible")
	}
	var combined struct {
		OK      bool
		License licensing.Status
	}
	if json.Unmarshal(status.Body.Bytes(), &combined) != nil || !combined.OK || combined.License.Valid || combined.License.State != "bridge" {
		t.Fatal("bridge status", status.Body)
	}
	var req licensing.Request
	r := f.call("GET", "/api/license/request", "", c, csrf)
	if r.Code != 200 || json.Unmarshal(r.Body.Bytes(), &req) != nil || req.Validate() != nil {
		t.Fatal("request", r.Body)
	}
	body := func(req licensing.Request) string {
		env, err := licensing.NewGrant(req, "迁移验证", key)
		if err != nil {
			t.Fatal(err)
		}
		b, err := json.Marshal(map[string]any{"license": env, "confirmed": true})
		if err != nil {
			t.Fatal(err)
		}
		return string(b)
	}
	valid := body(req)
	wrong := req
	wrong.DeviceCode, _ = licensing.Fingerprint(req.Kind, "1122334455667788")
	if f.call("POST", "/api/license/import", valid, c, "").Code != 403 || post("/api/license/import", body(wrong)) != 409 {
		t.Fatal("unsafe import")
	}
	if _, err := os.Stat(path); !os.IsNotExist(err) {
		t.Fatal("invalid import modified disk")
	}
	if post("/api/license/import", valid) != 200 {
		t.Fatal("import failed")
	}
	next := licensing.NewWithKeys(path, true, reader, keys)
	if !next.Allowed() || !next.Status().Permanent {
		t.Fatal("next core would not start")
	}
	if post("/api/upgrade/confirm", confirm) != 200 {
		t.Fatal("confirmation not forwarded to core")
	}
	if err := os.WriteFile(path, []byte("{}"), 0600); err != nil {
		t.Fatal(err)
	}
	if post("/api/upgrade/confirm", confirm) != 409 {
		t.Fatal("upgrade accepted damaged grant after validation")
	}
}

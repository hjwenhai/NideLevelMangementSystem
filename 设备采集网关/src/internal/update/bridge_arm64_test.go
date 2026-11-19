//go:build linux && arm64 && bridgeqa

package update

import (
	"bytes"
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/storage"
	"net"
	"net/http"
	"net/http/cookiejar"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Run under user-mode QEMU on an isolated Linux loopback. Start and stop
// processes in a temporary directory instead of running the board's init script.
func TestARM64Migration(t *testing.T) {
	root, err := os.Getwd()
	if err != nil {
		t.Fatal(err)
	}
	emulator := os.Getenv("BRIDGE_QEMU")
	grantFile := os.Getenv("BRIDGE_FIXTURE_GRANT")
	if emulator == "" || grantFile == "" {
		t.Skip("requires isolated QEMU and virtual-board grant")
	}
	base := t.TempDir()
	for _, dir := range []string{"releases", "config", "data", "logs", "sysroot/sys/devices/soc0"} {
		if err := os.MkdirAll(filepath.Join(base, dir), 0700); err != nil {
			t.Fatal(err)
		}
	}
	put(t, filepath.Join(base, "sysroot/sys/devices/soc0/serial_number"), []byte("0102030405060708"))
	old, bridge, next := "0.6.0-20261029-r3", "0.7.0-20261109-r2", bridgeTarget
	archive := func(rel string) string {
		return filepath.Join(root, "output/packages", "gt6757s-"+rel+"-linux-arm64.gwup")
	}
	read := func(p string) []byte {
		t.Helper()
		b, err := os.ReadFile(p)
		if err != nil {
			t.Fatal(err)
		}
		return b
	}
	trust := filepath.Join(base, "config/update-trust.json")
	put(t, trust, read(filepath.Join(root, "output/packages", "gt6757s-"+old+"-linux-arm64/deploy/update-trust.json")))
	startDir := filepath.Join(base, "releases", old)
	if err := os.Mkdir(startDir, 0700); err != nil {
		t.Fatal(err)
	}
	if _, err := VerifyBundle(archive(old), trust, "linux-arm64", startDir); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(startDir, filepath.Join(base, "current")); err != nil {
		t.Fatal(err)
	}
	randomAddr := func() string {
		l, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		defer l.Close()
		return l.Addr().String()
	}
	ipc, web, north := randomAddr(), randomAddr(), randomAddr()
	url := "http://" + web
	s, err := New(Options{Base: base, IPC: ipc, WebURL: url, Shell: "/bin/sh"})
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := config.Load(filepath.Join(root, "deploy/config.demo.json"))
	if err != nil {
		t.Fatal(err)
	}
	cfg.Meters = cfg.Meters[:1]
	cfg.Meters[0].Name = "客户试验罐"
	cfg.TCP.Listen = north
	cfg.FastMS = 1000
	cfg.History.PeriodSec = 1
	settings, _ := json.Marshal(cfg)
	cfgPath := filepath.Join(base, "config/gateway.json")
	put(t, cfgPath, settings)
	env := []byte("WEB_LISTEN=" + web + "\nTLS_CERT=\nTLS_KEY=\n")
	put(t, filepath.Join(base, "config/service.env"), env)
	cmd := func(rel, role string, flags ...string) *exec.Cmd {
		bin := filepath.Join(base, "releases", rel, "bin", role)
		// The emulator shipped by Docker expects argv[0] after the executable.
		args := append([]string{"-L", filepath.Join(base, "sysroot"), bin, bin}, flags...)
		c := exec.Command(emulator, args...)
		c.Dir = base
		return c
	}
	auth := filepath.Join(base, "data/admin.json")
	init := cmd(old, "gateway-web", "--init-admin", "--auth-file", auth)
	init.Stdin = strings.NewReader("Bridge-Fixture-Only-2026!\n")
	if out, err := init.CombinedOutput(); err != nil {
		t.Fatalf("init %v %s", err, out)
	}
	cred := read(auth)
	var procs []*exec.Cmd
	stop := func() {
		for _, p := range procs {
			_ = p.Process.Signal(os.Interrupt)
		}
		for _, p := range procs {
			_ = p.Wait()
		}
		procs = nil
	}
	t.Cleanup(stop)
	start := func(rel string) error {
		common := []string{"--ipc", ipc, "--token-file", "data/ipc.token", "--upgrade-base", base}
		core := cmd(rel, "gateway-core", append(common, "--simulate", "--config", cfgPath, "--db", "data/gateway-v2.db", "--service-script", filepath.Join(base, "current/deploy/service.sh"), "--upgrade-web-listen", web)...)
		server := cmd(rel, "gateway-web", append(common, "--listen", web, "--auth-file", auth)...)
		for _, p := range []*exec.Cmd{core, server} {
			p.Stdout = os.Stderr
			p.Stderr = os.Stderr
			if err := p.Start(); err != nil {
				return err
			}
			procs = append(procs, p)
		}
		return nil
	}
	if err := start(old); err != nil {
		t.Fatal(err)
	}
	jar, _ := cookiejar.New(nil)
	client := &http.Client{Jar: jar, Timeout: 60 * time.Second}
	var csrf string
	request := func(method, route string, body []byte, want int) []byte {
		t.Helper()
		req, err := http.NewRequest(method, url+route, bytes.NewReader(body))
		if err != nil {
			t.Fatal(err)
		}
		req.Header.Set("X-CSRF-Token", csrf)
		resp, err := client.Do(req)
		if err != nil {
			t.Fatal(route, err)
		}
		defer resp.Body.Close()
		out, err := io.ReadAll(resp.Body)
		if err != nil || resp.StatusCode != want {
			t.Fatalf("%s: %d %s %v", route, resp.StatusCode, out, err)
		}
		return out
	}
	login := func() {
		deadline := time.Now().Add(25 * time.Second)
		for {
			resp, err := client.Get(url)
			if err == nil {
				resp.Body.Close()
				break
			}
			if time.Now().After(deadline) {
				t.Fatal(err)
			}
			time.Sleep(100 * time.Millisecond)
		}
		b := request("POST", "/api/login", []byte(`{"password":"Bridge-Fixture-Only-2026!"}`), 200)
		var session struct{ CSRF string }
		if json.Unmarshal(b, &session) != nil || session.CSRF == "" {
			t.Fatal("login")
		}
		csrf = session.CSRF
	}
	login()
	db, err := sql.Open("sqlite", filepath.Join(base, "data/gateway-v2.db"))
	if err != nil {
		t.Fatal(err)
	}
	db.SetMaxIdleConns(0) // Query the database at its live path after each switch.
	defer db.Close()
	var historyMs, level int64
	deadline := time.Now().Add(30 * time.Second)
	for db.QueryRow("SELECT record_ms,level FROM history WHERE slot=1 AND valid<>0 LIMIT 1").Scan(&historyMs, &level) != nil {
		if time.Now().After(deadline) {
			t.Fatal("0.6.0 history missing")
		}
		time.Sleep(150 * time.Millisecond)
	}
	upgrade := func(target string, beforeConfirm func(string)) {
		t.Helper()
		data := request("POST", "/api/upgrade/upload", read(archive(target)), 200)
		var st Status
		if json.Unmarshal(data, &st) != nil || st.Phase != "ready" || st.Target != target {
			t.Fatal("stage", string(data))
		}
		if beforeConfirm != nil {
			beforeConfirm(st.ID)
		}
		if err := s.Begin(st.ID, "web:127.0.0.1"); err != nil {
			t.Fatal(err)
		}
		ops := s.defaultOps()
		ops.Delay = 0
		ops.Service = func(ctx context.Context, rel, action string) error {
			if action == "stop" {
				stop()
				return nil
			}
			return start(rel)
		}
		ops.CheckConfig = func(ctx context.Context, rel string) error {
			return cmd(rel, "gateway-core", "--check-config", "--config", cfgPath).Run()
		}
		ops.Backup = storage.BackupDatabase
		if err := s.Run(st.ID, &ops); err != nil {
			t.Fatal("switch", err)
		}
		if status := s.Status(); status.Phase != "succeeded" || status.Current != target {
			t.Fatal(status)
		}
		login()
		conn, err := net.DialTimeout("tcp", north, 3*time.Second)
		if err != nil {
			t.Fatal(err)
		}
		defer conn.Close()
		conn.SetDeadline(time.Now().Add(3 * time.Second))
		if _, err = conn.Write([]byte{0, 1, 0, 0, 0, 6, 1, 4, 0, 0, 0, 4}); err != nil {
			t.Fatal(err)
		}
		var reply [17]byte
		if _, err = io.ReadFull(conn, reply[:]); err != nil || reply[7] != 4 || reply[8] != 8 || reply[16] != 64 {
			t.Fatalf("Modbus data at original address %x %v", reply, err)
		}
		if !bytes.Equal(read(cfgPath), settings) || !bytes.Equal(read(auth), cred) || !bytes.Equal(read(filepath.Join(base, "config/service.env")), env) {
			t.Fatal("site data changed")
		}
		var v int64
		if err := db.QueryRow("SELECT level FROM history WHERE record_ms=? AND slot=1", historyMs).Scan(&v); err != nil || v != level {
			t.Fatal("old history lost", err)
		}
		t.Log("verified release switch, web/core probe, site settings and original history", target)
	}
	upgrade(bridge, nil)
	upgrade(next, func(id string) {
		conf, _ := json.Marshal(map[string]any{"id": id, "confirmed": true})
		request("POST", "/api/upgrade/confirm", conf, 409)
		req := request("GET", "/api/license/request", nil, 200)
		var application struct {
			DeviceCode string `json:"device_code"`
		}
		json.Unmarshal(req, &application)
		var env struct {
			License struct {
				DeviceCode string `json:"device_code"`
			} `json:"license"`
		}
		grant := read(grantFile)
		if json.Unmarshal(grant, &env) != nil || len(application.DeviceCode) != 64 || application.DeviceCode != env.License.DeviceCode {
			t.Fatal("hardware binding", string(req))
		}
		body, _ := json.Marshal(map[string]any{"license": json.RawMessage(grant), "confirmed": true})
		saved := request("POST", "/api/license/import", body, 200)
		if !bytes.Contains(saved, []byte(`"file_valid":true`)) {
			t.Fatal("grant not saved", string(saved))
		}
	})
	final := cmd(next, "gateway-core", "--check-license", "--license-file", filepath.Join(base, "config/license.json"))
	if out, err := final.CombinedOutput(); err != nil || !strings.Contains(string(out), "本机永久授权校验通过") {
		t.Fatal(fmt.Sprint(err), string(out))
	}
	t.Log("signed 0.8.1 binary verified the grant imported by the bridge")
}

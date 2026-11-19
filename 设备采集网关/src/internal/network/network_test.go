package network

import (
	"bytes"
	"fmt"
	"levelgateway/src/internal/config"
	"net"
	"os"
	"path/filepath"
	"reflect"
	"strings"
	"testing"
	"time"
)

func fixture(t *testing.T) *Service {
	t.Helper()
	s := &Service{Opts: Options{Base: t.TempDir(), Fixture: true}}
	for i, name := range []string{"net.conf", "net2.conf"} {
		p := Interface{Name: fmt.Sprintf("eth%d", i), IP: []string{"192.168.1.230", "192.168.0.230"}[i], Mask: "255.255.255.0"}
		if e := config.WriteAtomic(s.vendor(name), VendorFile([]byte("# original\nOTHER=yes\n"), p)); e != nil {
			t.Fatal(e)
		}
	}
	c := config.Default(1)
	c.TCP.Listen = "192.168.0.230:502"
	if e := config.SaveAtomic(s.paths()[2], c); e != nil {
		t.Fatal(e)
	}
	if e := config.WriteAtomic(s.paths()[3], []byte("# settings\nWEB_LISTEN=192.168.1.230:8088\nWATCHDOG=\nHARDWARE_WRITES=0\n")); e != nil {
		t.Fatal(e)
	}
	return s
}
func candidate(s *Service) Settings {
	c, _ := s.settings()
	for i := range c.Interfaces {
		c.Interfaces[i].IP = strings.TrimSuffix(c.Interfaces[i].IP, "230") + "231"
	}
	return c
}
func waitPhase(t *testing.T, s *Service, phase string) State {
	t.Helper()
	until := time.Now().Add(6 * time.Second)
	for time.Now().Before(until) {
		st, _ := s.read()
		if st.Phase == phase {
			return st
		}
		time.Sleep(5 * time.Millisecond)
	}
	t.Fatalf("phase %s: %+v", phase, s.Status())
	return State{}
}
func TestSettingsValidation(t *testing.T) {
	good := candidate(fixture(t))
	if e := good.Validate(); e != nil {
		t.Fatal(e)
	}
	cases := []struct {
		name   string
		change func(*Settings)
	}{
		{"bad name", func(c *Settings) { c.Interfaces[0].Name = "eth0;reboot" }},
		{"bad ip", func(c *Settings) { c.Interfaces[0].IP = "192.168.1.231;id" }},
		{"network", func(c *Settings) { c.Interfaces[0].IP = "192.168.1.0" }},
		{"broadcast", func(c *Settings) { c.Interfaces[0].IP = "192.168.1.255" }},
		{"loopback", func(c *Settings) { c.Interfaces[0].IP = "127.0.0.1" }},
		{"multicast", func(c *Settings) { c.Interfaces[0].IP = "224.0.0.1" }},
		{"mask", func(c *Settings) { c.Interfaces[0].Mask = "255.0.255.0" }},
		{"host mask", func(c *Settings) { c.Interfaces[0].Mask = "255.255.255.255" }},
		{"overlap", func(c *Settings) { c.Interfaces[0].Mask = "255.255.0.0" }},
		{"two gateways", func(c *Settings) { c.Interfaces[0].Gateway = "192.168.1.1"; c.Interfaces[1].Gateway = "192.168.0.1" }},
		{"off subnet gateway", func(c *Settings) { c.Interfaces[1].Gateway = "10.0.0.1" }},
		{"self gateway", func(c *Settings) { c.Interfaces[1].Gateway = c.Interfaces[1].IP }},
		{"invalid TCP interface", func(c *Settings) { c.TCPInterface = "eth2" }},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := good
			c.Interfaces = append([]Interface(nil), good.Interfaces...)
			tc.change(&c)
			if c.Validate() == nil {
				t.Fatal("invalid accepted")
			}
		})
	}
}
func TestVendorFileAndWildcard(t *testing.T) {
	p, e := ParseVendor("eth0", []byte("METHOD='STATIC'\nIPADDR=192.168.1.230\nNETMASK=255.255.255.0\n#GATEWAY=x\n"))
	if e != nil || p.Gateway != "" {
		t.Fatal(p, e)
	}
	if _, e = ParseVendor("eth0", []byte("METHOD=DHCP")); e == nil {
		t.Fatal("DHCP accepted")
	}
	b := VendorFile([]byte("# preserve\nDNS=old\nexport IPADDR=old\n IPADDR=duplicate\n"), p)
	if bytes.Contains(b, []byte("\nGATEWAY=")) || !bytes.Contains(b, []byte("#GATEWAY=\n")) {
		t.Fatal("empty gateway must remain disabled in vendor startup", string(b))
	}
	if bytes.Count(b, []byte("IPADDR=")) != 1 || !bytes.Contains(b, []byte("DNS=old")) {
		t.Fatal(string(b))
	}
	if v, e := Wildcard("192.168.1.230:8088"); e != nil || v != "0.0.0.0:8088" {
		t.Fatal(v, e)
	}
}
func TestTransactionManualConfirmAndPersistence(t *testing.T) {
	s := fixture(t)
	c := candidate(s)
	st, e := s.Begin(c, "web:test")
	if e != nil {
		t.Fatal(e)
	}
	if !s.Busy() {
		t.Fatal("missing durable gate")
	}
	if _, e = s.Begin(c, "web:test"); e == nil {
		t.Fatal("duplicate accepted")
	}
	done := make(chan error, 1)
	go func() {
		done <- s.RunWorker(st.ID, WorkerOps{Stop: func() error { return nil }, Start: func() error { return nil }, Apply: func(c Settings) error { return c.Validate() }}, 3*time.Second)
	}()
	waitPhase(t, s, "trial")
	if _, e = s.Confirm(st.ID, "192.168.1.230", "web:test"); e == nil {
		t.Fatal("old destination accepted")
	}
	if _, e = s.Confirm("wrong", c.Interfaces[0].IP, "web:test"); e == nil {
		t.Fatal("wrong ID accepted")
	}
	if _, e = s.Confirm(st.ID, c.Interfaces[0].IP, "web:test"); e != nil {
		t.Fatal(e)
	}
	if e = <-done; e != nil {
		t.Fatal(e)
	}
	if s.Busy() {
		t.Fatal("gate not cleared")
	}
	current, _ := s.settings()
	if !reflect.DeepEqual(current, c) {
		t.Fatal(current, c)
	}
	cfg, e := config.Load(s.paths()[2])
	if e != nil || cfg.TCP.Listen != "192.168.0.231:502" || cfg.Revision != 2 || cfg.Meters[0].Binding != 1 || cfg.Meters[0].Address != 1 {
		t.Fatal(cfg, e)
	}
	b, _ := os.ReadFile(s.paths()[3])
	if envValue(b, "WEB_LISTEN") != "0.0.0.0:8088" || !bytes.Contains(b, []byte("WATCHDOG=\n")) {
		t.Fatal(string(b))
	}
	// A new service instance on a later boot leaves confirmed files untouched.
	next := &Service{Opts: s.Opts}
	if e = next.RecoverBoot(); e != nil {
		t.Fatal(e)
	}
	after, _ := next.settings()
	if !reflect.DeepEqual(after, c) {
		t.Fatal("confirmed setting lost")
	}
}
func TestTimeoutAndPartialFailureRestoreExactFiles(t *testing.T) {
	for _, fail := range []bool{false, true} {
		t.Run(fmt.Sprint(fail), func(t *testing.T) {
			s := fixture(t)
			st, e := s.Begin(candidate(s), "web:test")
			if e != nil {
				t.Fatal(e)
			}
			before, _ := s.read()
			calls := 0
			starts := 0
			ops := WorkerOps{Stop: func() error { return nil }, Start: func() error { starts++; return nil }, Apply: func(Settings) error {
				calls++
				if fail && calls == 1 {
					return fmt.Errorf("injected ip failure")
				}
				return nil
			}}
			if e = s.RunWorker(st.ID, ops, 20*time.Millisecond); e != nil {
				t.Fatal(e)
			}
			if waitPhase(t, s, "rolled_back").PID != 0 || s.Busy() {
				t.Fatal("rollback gate")
			}
			for _, f := range before.Files {
				b, _ := os.ReadFile(f.Path)
				if !bytes.Equal(b, f.Data) {
					t.Fatal("backup differs", f.Path)
				}
			}
			if calls != 2 || starts < 1 {
				t.Fatal(calls, starts)
			}
		})
	}
}
func TestBootRecoveryAllPendingPhasesAndTamper(t *testing.T) {
	for _, phase := range []string{"queued", "applying", "trial", "confirming", "rolling_back", "failed"} {
		t.Run(phase, func(t *testing.T) {
			s := fixture(t)
			_, e := s.Begin(candidate(s), "web:test")
			if e != nil {
				t.Fatal(e)
			}
			st, _ := s.read()
			st.Phase = phase
			st.DeadlineMS = time.Now().Add(time.Hour).UnixMilli()
			if e = s.save(st); e != nil {
				t.Fatal(e)
			}
			if e = s.changeFiles(st); e != nil {
				t.Fatal(e)
			}
			if e = s.RecoverBoot(); e != nil {
				t.Fatal(e)
			}
			for _, f := range st.Files {
				b, _ := os.ReadFile(f.Path)
				if !bytes.Equal(b, f.Data) {
					t.Fatal("power loss restoration differs")
				}
			}
			if s.Status().Phase != "rolled_back" {
				t.Fatal(s.Status())
			}
		})
	}
	s := fixture(t)
	st, _ := s.Begin(candidate(s), "web:test")
	if e := s.AbortUnlaunched(st.ID); e != nil {
		t.Fatal(e)
	}
	if e := config.WriteAtomic(s.path("data", "network-state.json"), []byte("broken")); e != nil {
		t.Fatal(e)
	}
	if !s.Busy() || s.RecoverBoot() == nil {
		t.Fatal("corruption must fail closed")
	}
}
func TestRestoreRejectsOutsidePath(t *testing.T) {
	s := fixture(t)
	_, _ = s.Begin(candidate(s), "web:test")
	st, _ := s.read()
	outside := filepath.Join(t.TempDir(), "untouched")
	st.Files[0].Path = outside
	if s.restore(st, func(Settings) error { return nil }) == nil {
		t.Fatal("tampered path accepted")
	}
	if _, e := os.Stat(outside); !os.IsNotExist(e) {
		t.Fatal("outside file touched")
	}
}
func TestGatewayAndListeners(t *testing.T) {
	s := fixture(t)
	c := candidate(s)
	c.Interfaces[1].Gateway = "192.168.0.1"
	if c.Validate() != nil {
		t.Fatal("valid gateway rejected")
	}
	c.TCPInterface = "eth0"
	if listenFor(c, "502") != net.JoinHostPort(c.Interfaces[0].IP, "502") {
		t.Fatal("bind mismatch")
	}
	c.TCPInterface = "both"
	if listenFor(c, "502") != "0.0.0.0:502" {
		t.Fatal("wildcard mismatch")
	}
}

func TestServiceStartFailureRestoresOldConfiguration(t *testing.T) {
	s := fixture(t)
	job, e := s.Begin(candidate(s), "web:test")
	if e != nil {
		t.Fatal(e)
	}
	old, _ := s.read()
	starts := 0
	ops := WorkerOps{Stop: func() error { return nil }, Start: func() error {
		starts++
		if starts == 1 {
			return fmt.Errorf("injected start failure")
		}
		return nil
	}, Apply: func(Settings) error { return nil }}
	if e = s.RunWorker(job.ID, ops, time.Second); e != nil {
		t.Fatal(e)
	}
	for _, f := range old.Files {
		b, _ := os.ReadFile(f.Path)
		if !bytes.Equal(b, f.Data) {
			t.Fatal("start failure did not restore", f.Path)
		}
	}
	if starts != 2 || s.Busy() {
		t.Fatal(starts, s.Status())
	}
}
func TestTrialJournalFailureStillRestoresFiles(t *testing.T) {
	s := fixture(t)
	job, e := s.Begin(candidate(s), "web:test")
	if e != nil {
		t.Fatal(e)
	}
	old, _ := s.read()
	calls := 0
	ops := WorkerOps{Stop: func() error { return nil }, Start: func() error { return nil }, Apply: func(Settings) error {
		calls++
		if calls == 1 {
			p := s.path("data", "network-state.json")
			if e := os.Rename(p, p+".interrupted"); e != nil {
				return e
			}
			return os.Mkdir(p, 0700)
		}
		return nil
	}}
	if e = s.RunWorker(job.ID, ops, time.Second); e == nil {
		t.Fatal("journal failure should be reported")
	}
	for _, f := range old.Files {
		b, _ := os.ReadFile(f.Path)
		if !bytes.Equal(b, f.Data) {
			t.Fatal("journal failure did not restore", f.Path)
		}
	}
	if calls != 2 || !s.Busy() {
		t.Fatal("storage failure must retain closed gate", calls)
	}
}

func TestNetworkReservationsAndOversizeFailWithoutMutation(t *testing.T) {
	for _, name := range []string{"install.lock", "upgrades/upload.lock"} {
		t.Run(name, func(t *testing.T) {
			s := fixture(t)
			path := s.path("data", name)
			if e := os.MkdirAll(path, 0700); e != nil {
				t.Fatal(e)
			}
			if _, e := s.Begin(candidate(s), "web:test"); e == nil {
				t.Fatal("concurrent maintenance accepted")
			}
			if s.Busy() {
				t.Fatal("failed request left network gate")
			}
		})
	}
	s := fixture(t)
	p := s.paths()[3]
	old, _ := os.ReadFile(p)
	old = append(old, []byte("#"+strings.Repeat("x", 66000)+"\n")...)
	if e := config.WriteAtomic(p, old); e != nil {
		t.Fatal(e)
	}
	if _, e := s.Begin(candidate(s), "web:test"); e == nil {
		t.Fatal("oversized journal accepted")
	}
	if s.Busy() {
		t.Fatal("oversized request left gate")
	}
	for _, name := range []string{"install.lock", "upgrades/upload.lock"} {
		if _, e := os.Stat(s.path("data", name)); !os.IsNotExist(e) {
			t.Fatal("reservation leaked", name)
		}
	}
}

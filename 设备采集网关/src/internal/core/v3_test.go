package core

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"levelgateway/src/internal/buildinfo"
	"levelgateway/src/internal/modbus"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
)

func fc06(a, v uint16) []byte {
	p := make([]byte, 5)
	p[0] = 6
	binary.BigEndian.PutUint16(p[1:], a)
	binary.BigEndian.PutUint16(p[3:], v)
	return p
}
func TestNorthConcurrentDuplicateACK(t *testing.T) {
	m := testManager(t, 1)
	var wg sync.WaitGroup
	for i := 0; i < 8; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			p, e := tcpRequest(m.Config().TCP.Listen, fc06(30000, 5))
			if e != nil || !bytes.Equal(p, fc06(30000, 5)) {
				t.Errorf("duplicate ACK %x %v", p, e)
			}
		}()
	}
	wg.Wait()
	until(t, func() bool { return m.Engine.Results()[0].Main.State == 3 })
	if m.Ports[1].Simulator.Count(1) != 1 || m.Engine.Results()[0].Main.Count != 1 {
		t.Fatal("north concurrent duplicate sent twice")
	}
}
func TestD2ProtocolShapesPermissionsDiscoveryAndNoPartialWrite(t *testing.T) {
	m := testManager(t, 32)
	w, err := m.Registers(3, 0, 30)
	if err != nil || w[1] != 3 || w[2] != 3 || w[28] != 3 || w[29] != 12 {
		t.Fatal(w, err)
	}
	cases := []struct {
		name string
		pdu  []byte
		code byte
	}{
		{"reserved command", fc06(30000, 8), 3}, {"stop bad value", fc06(30007, 2), 3}, {"state read only", fc06(30001, 5), 2}, {"reserved read only", fc06(30003, 0), 2},
		{"D1 two words", modbus.WritePDU(30000, []uint16{5, 1}), 3}, {"cross slot", modbus.WritePDU(30011, []uint16{0, 5}), 3}, {"legacy", modbus.WritePDU(40512, make([]uint16, 64)), 2},
		{"coil", []byte{5, 0x75, 0x30, 0xff, 0}, 1}, {"FC15", []byte{15}, 1}, {"FC06 truncated", []byte{6, 0x75, 0x30}, 3}, {"slot65", fc06(30768, 5), 2},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			p := m.Handle("tcp:test", 1, tc.pdu)
			if len(p) != 2 || p[0] != tc.pdu[0]|128 || p[1] != tc.code {
				t.Fatalf("%x", p)
			}
			if m.Engine.Results()[0].Main.Count != 0 || m.Engine.Results()[0].Stop.Count != 0 {
				t.Fatal("partial side effect")
			}
		})
	}
	for _, a := range []uint16{30000, 30012, 30372} {
		if _, e := m.Registers(3, a, 12); e != nil {
			t.Fatal(e)
		}
	}
	if _, err = m.Registers(3, 30767, 2); err != modbus.IllegalAddress {
		t.Fatal("region end")
	}
	if _, err = m.Registers(4, 30000, 1); err != modbus.IllegalAddress {
		t.Fatal("FC04 alias")
	}
	c := m.Config()
	c.TCP.AllowWrites = false
	if err = m.Apply(c); err != nil {
		t.Fatal(err)
	}
	if p := m.Handle("tcp:test", 1, fc06(30000, 5)); p[1] != 2 {
		t.Fatal("write permission bypass")
	}
	c = m.Config()
	c.TCP.AllowWrites = true
	c.Actions = nil
	if err = m.Apply(c); err != nil {
		t.Fatal(err)
	}
	if p := m.Handle("tcp:test", 1, fc06(30000, 5)); p[1] != 3 {
		t.Fatal("allowlist bypass")
	}
	if p := m.Handle("tcp:test", 0, fc06(30000, 5)); p != nil {
		t.Fatal("broadcast accepted")
	}
}
func TestD2TerminalResetConfigGuardAndMaintenanceAPI(t *testing.T) {
	m := testManager(t, 1)
	if p := m.Handle("tcp:test", 1, fc06(30000, 5)); !bytes.Equal(p, fc06(30000, 5)) {
		t.Fatal(p)
	}
	until(t, func() bool { return m.Engine.Results()[0].Main.State == 3 })
	c := m.Config()
	c.Meters[0].Address = 2
	if m.Apply(c) == nil {
		t.Fatal("rebound terminal without reset")
	}
	if p := m.Handle("tcp:test", 1, fc06(30000, 0)); p[0] != 6 {
		t.Fatal(p)
	}
	if err := m.Apply(c); err != nil {
		t.Fatal(err)
	}
	// Create a pending cycle on a port whose polling loop is temporarily occupied.
	m.applyMu.Lock()
	m.ApplyingSinceMS.Store(1)
	p := m.Handle("tcp:test", 1, fc06(30000, 5))
	m.ApplyingSinceMS.Store(0)
	m.applyMu.Unlock()
	if p[1] != 6 {
		t.Fatal("reconfiguration busy gate")
	}
	h := m.API("secret")
	body := `{"slot":1,"stop":false,"id":"stale","operator":"01","reason":"现场核查","site_verified":true}`
	req := httptest.NewRequest("POST", "/api/control/maintenance-reset", strings.NewReader(body))
	w := httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code != 401 {
		t.Fatal("unauthenticated reset")
	}
	req = httptest.NewRequest("POST", "/api/control/maintenance-reset", strings.NewReader(body))
	req.Header.Set("Authorization", "Bearer secret")
	w = httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code != 409 {
		t.Fatal("stale/nonunknown reset")
	}
	req = httptest.NewRequest("GET", "/api/status", nil)
	req.Header.Set("Authorization", "Bearer secret")
	w = httptest.NewRecorder()
	h.ServeHTTP(w, req)
	var status map[string]any
	json.Unmarshal(w.Body.Bytes(), &status)
	if status["version"] != buildinfo.Version || status["map_version"] != float64(3) || w.Header().Get("X-Gateway-Map") != "3" {
		t.Fatal(status)
	}
}

package core

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/fw9000"
	"levelgateway/src/internal/modbus"
	"levelgateway/src/internal/storage"
	"net"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"sync"
	"testing"
	"time"
)

func testManager(t *testing.T, n int) *Manager {
	t.Helper()
	d := t.TempDir()
	c := config.Default(n)
	c.FastMS = 1000
	c.TCP.AllowWrites = true
	c.Actions = []uint16{2, 4}
	c.History.PeriodSec = 1
	l, e := net.Listen("tcp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	c.TCP.Listen = l.Addr().String()
	l.Close()
	p := filepath.Join(d, "config.json")
	if e = config.SaveAtomic(p, c); e != nil {
		t.Fatal(e)
	}
	s, e := storage.Open(filepath.Join(d, "db"))
	if e != nil {
		t.Fatal(e)
	}
	m, e := New(context.Background(), c, p, s, true, false)
	if e != nil {
		s.Close()
		t.Fatal(e)
	}
	t.Cleanup(func() { m.Close(); s.Close() })
	return m
}
func until(t *testing.T, fn func() bool) {
	t.Helper()
	end := time.Now().Add(6 * time.Second)
	for time.Now().Before(end) {
		if fn() {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("condition timeout")
}
func tcpRequest(addr string, pdu []byte) ([]byte, error) {
	c, e := net.DialTimeout("tcp", addr, time.Second)
	if e != nil {
		return nil, e
	}
	defer c.Close()
	c.SetDeadline(time.Now().Add(2 * time.Second))
	h := make([]byte, 7)
	binary.BigEndian.PutUint16(h, 123)
	binary.BigEndian.PutUint16(h[4:], uint16(len(pdu)+1))
	h[6] = 1
	if e = modbus.WriteAll(c, append(h, pdu...)); e != nil {
		return nil, e
	}
	if _, e = io.ReadFull(c, h); e != nil {
		return nil, e
	}
	if binary.BigEndian.Uint16(h) != 123 || h[6] != 1 {
		return nil, io.ErrUnexpectedEOF
	}
	out := make([]byte, int(binary.BigEndian.Uint16(h[4:]))-1)
	_, e = io.ReadFull(c, out)
	return out, e
}
func TestPipelineTCPAndD2Control(t *testing.T) {
	m := testManager(t, 32)
	until(t, func() bool { return m.Views()[31].Valid == 127 })
	var wg sync.WaitGroup
	for client := 0; client < 4; client++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < 8; i++ {
				p, e := tcpRequest(m.Config().TCP.Listen, modbus.ReadPDU(4, 1000, 120))
				if e != nil {
					t.Error(e)
					return
				}
				w, e := modbus.DecodeWords(p, 120)
				if e != nil || w[1] != 127 || modbus.Get32(w[4:]) != 100123 {
					t.Errorf("invalid snapshot: %v", e)
					return
				}
			}
		}()
	}
	wg.Wait()
	p := []byte{6, 0x75, 0x30, 0, 5} // FC06, 30000, up
	reply, e := tcpRequest(m.Config().TCP.Listen, p)
	if e != nil || len(reply) != 5 || reply[0] != 6 {
		t.Fatalf("write %x %v", reply, e)
	}
	until(t, func() bool { return m.Engine.Results()[0].Main.State == command.Confirmed })
	reply, e = tcpRequest(m.Config().TCP.Listen, p)
	if e != nil || reply[0] != 6 || m.Ports[1].Simulator.Count(1) != 1 {
		t.Fatal("duplicate action")
	}
	if x := m.Handle("tcp:test", 1, modbus.WritePDU(1000, []uint16{1})); len(x) != 2 || x[1] != 2 {
		t.Fatal("cache writable")
	}
	if x := m.Handle("tcp:test", 1, modbus.WritePDU(40000, make([]uint16, 64))); x[0] != 0x90 || x[1] != 2 {
		t.Fatal("legacy mailbox accepted")
	}
	if _, e = m.Registers(4, 30000, 12); e != modbus.IllegalAddress {
		t.Fatal("control input alias")
	}
	until(t, func() bool {
		rows, e := m.Store.Query(0, time.Now().UnixMilli(), 0, 0, 1, 100)
		return e == nil && len(rows) > 0
	})
}
func TestReconfigureTombstoneAndRollback(t *testing.T) {
	m := testManager(t, 2)
	until(t, func() bool { return m.Views()[0].Valid == 127 })
	c := m.Config()
	c.Meters = c.Meters[1:]
	if e := m.Apply(c); e != nil {
		t.Fatal(e)
	}
	if m.Views()[0].Valid != 0 || m.Config().SlotVersions[0] != 2 {
		t.Fatal("removed slot retained data")
	}
	c = m.Config()
	c.Meters = append(c.Meters, config.Meter{Slot: 1, Port: 2, Address: 1, Binding: 2, Enabled: true, Name: "new"})
	if e := m.Apply(c); e != nil {
		t.Fatal(e)
	}
	if m.Config().SlotVersions[0] != 3 || m.Config().Meters[1].Binding != 3 {
		t.Fatal("slot reuse lost tombstone")
	}
	c = m.Config()
	before := c.Revision
	occupied, e := net.Listen("tcp", "127.0.0.1:0")
	if e != nil {
		t.Fatal(e)
	}
	defer occupied.Close()
	c.TCP.Listen = occupied.Addr().String()
	if e = m.Apply(c); e == nil {
		t.Fatal("bind failure accepted")
	}
	if m.Config().Revision != before {
		t.Fatal("failed candidate committed")
	}
	if _, e = tcpRequest(m.Config().TCP.Listen, modbus.ReadPDU(4, 0, 24)); e != nil {
		t.Fatal("old listener not restored", e)
	}
	if e = m.Apply(config.Config{}); e == nil {
		t.Fatal("invalid config panic/accepted")
	}
	if e = m.Apply(c); e == nil {
		t.Fatal("occupied bind accepted")
	}
}
func TestCapacityAndFaultIsolation(t *testing.T) {
	for _, n := range []int{16, 24, 32} {
		t.Run(string(rune(n)), func(t *testing.T) {
			m := testManager(t, n)
			until(t, func() bool { return m.Views()[n-1].Valid == 127 })
			if !m.Healthy() {
				t.Fatal("healthy sim failed")
			}
		})
	}
	m := testManager(t, 32)
	until(t, func() bool { return m.Views()[31].Valid == 127 })
	old := m.Views()[0].Groups[0].TimeMS
	m.Ports[1].Simulator.SetFault(1, fw9000.Fault{Offline: true, UnsupportedStart: -1})
	until(t, func() bool { return m.Views()[0].Valid == 0 })
	if m.Views()[16].Valid != 127 || m.Views()[0].Groups[0].TimeMS != old {
		t.Fatal("fault affected another bus/time")
	}
	m.Ports[1].Simulator.SetFault(1, fw9000.Fault{UnsupportedStart: -1})
	until(t, func() bool { return m.Views()[0].Valid == 127 })
}
func TestAPIAuthJSONAndTime(t *testing.T) {
	m := testManager(t, 1)
	h := m.API("secret")
	req := httptest.NewRequest("GET", "/api/config", nil)
	w := httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code != 401 {
		t.Fatal("unauthenticated API")
	}
	b, _ := json.Marshal(m.Config())
	req = httptest.NewRequest("PUT", "/api/config", strings.NewReader(string(b)+"{}"))
	req.Header.Set("Authorization", "Bearer secret")
	w = httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code == http.StatusOK {
		t.Fatal("trailing JSON accepted")
	}
	req = httptest.NewRequest("PUT", "/api/simulator/fault", strings.NewReader("{\"port\":1,\"address\":1,\"fault\":{}}"))
	req.Header.Set("Authorization", "Bearer secret")
	w = httptest.NewRecorder()
	h.ServeHTTP(w, req)
	if w.Code != 200 {
		t.Fatal(w.Body.String())
	}
	until(t, func() bool { return m.Views()[0].Valid == 127 })
	if e := m.CalibrateClock(time.Now().UnixMilli(), false); e != nil {
		t.Fatal(e)
	}
	until(t, func() bool { return m.Views()[0].Quality&128 == 0 })
	if e := m.CalibrateClock(time.Now().Add(-time.Hour).UnixMilli(), true); e == nil {
		t.Fatal("simulation changed host clock")
	}
}

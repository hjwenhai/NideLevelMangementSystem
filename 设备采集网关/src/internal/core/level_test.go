package core

import (
	"bytes"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/modbus"
	"net/http/httptest"
	"testing"
)

func TestRemoteLevelNorthShapePermissionAndWebFlow(t *testing.T) {
	m := testManager(t, 64)
	if p := m.Handle("tcp:test", 1, modbus.WritePDU(31000, []uint16{0, 100})); p[0] != 0x90 || p[1] != 3 {
		t.Fatal("permission default enabled", p)
	}
	c := m.Config()
	c.Actions = append(c.Actions, command.LevelAdjustPermission)
	if err := m.Apply(c); err != nil {
		t.Fatal(err)
	}
	for _, p := range [][]byte{fc06(31000, 1), modbus.WritePDU(31001, []uint16{0, 100}), modbus.WritePDU(31000, []uint16{0, 1, 2}), modbus.WritePDU(31256, []uint16{0, 1})} {
		reply := m.Handle("tcp:test", 1, p)
		if reply[0]&128 == 0 {
			t.Fatalf("invalid shape: %x", p)
		}
	}
	a := uint16(31000 + 4*63)
	if p := m.Handle("tcp:test", 1, modbus.WritePDU(a, []uint16{0, 15000})); p[0] != 16 {
		t.Fatal(p)
	}
	ready, _ := m.Registers(3, a, 4)
	if ready[2] != 1 {
		t.Fatal(ready)
	}
	if _, err := m.Registers(4, a, 2); err != modbus.IllegalAddress {
		t.Fatal("FC04 alias")
	}
	if p := m.Handle("tcp:test", 1, fc06(30000+12*63, 11)); p[0] != 6 {
		t.Fatal(p)
	}
	until(t, func() bool { return m.Engine.Results()[63].Main.State == 3 })
	if m.Ports[4].Simulator.Meters[16].OffsetWriteCount != 1 {
		t.Fatal("wrong target")
	}
	if m.Engine.Submit(64, false, 0, "tcp:test") != nil {
		t.Fatal("reset")
	}
	c = m.Config()
	c.TCP.AllowWrites = false
	if err := m.Apply(c); err != nil {
		t.Fatal(err)
	}
	api := m.API("test-token")
	call := func(data string, auth bool) *httptest.ResponseRecorder {
		r := httptest.NewRequest("POST", "/api/control/level-adjust", bytes.NewBufferString(data))
		if auth {
			r.Header.Set("Authorization", "Bearer test-token")
		}
		w := httptest.NewRecorder()
		api.ServeHTTP(w, r)
		return w
	}
	good := `{"slot":64,"target_raw":16000,"operator":"现场01","reason":"尺量液位校准","site_verified":true}`
	if call(`{"slot":64,"operator":"现场01","reason":"尺量液位校准","site_verified":true}`, true).Code != 400 {
		t.Fatal("missing target interpreted as zero")
	}
	if call(good, false).Code != 401 || call(`{"slot":64,"target_raw":16000}`, true).Code != 400 || call(good+`{}`, true).Code != 400 {
		t.Fatal("auth/confirmation")
	}
	if w := call(good, true); w.Code != 202 {
		t.Fatal(w.Code, w.Body.String())
	}
	until(t, func() bool { return m.Engine.Results()[63].Main.State == 3 })
	cycle := m.Engine.Results()[63].Main
	if cycle.Request.Operator != "现场01" || cycle.Level == nil || !cycle.Level.PasswordCleared {
		t.Fatal(cycle)
	}
	until(t, func() bool { return m.Views()[63].LevelMM == 1600 })
}

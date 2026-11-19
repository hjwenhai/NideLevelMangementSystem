package core

import (
	"bytes"
	"encoding/json"
	"levelgateway/src/internal/fw9000"
	"levelgateway/src/internal/modbus"
	"strings"
	"testing"
)

func TestNorthManualResetTCPAndRTUShapes(t *testing.T) {
	for _, tc := range []struct {
		name, source string
		stop, multi  bool
	}{
		{"tcp-main-FC06", "tcp:test", false, false},
		{"rtu-main-FC10", "rtu:COM3", false, true},
		{"tcp-stop-FC10", "tcp:test", true, true},
		{"rtu-stop-FC06", "rtu:COM3", true, false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			m := testManager(t, 1)
			cfg := m.Config()
			cfg.Ports[2].AllowWrites = true
			if err := m.Apply(cfg); err != nil {
				t.Fatal(err)
			}
			addr, value := uint16(30000), uint16(5)
			if tc.stop {
				addr, value = 30007, 1
			}
			write := func(v uint16) []byte {
				pdu := fc06(addr, v)
				if tc.multi {
					pdu = modbus.WritePDU(addr, []uint16{v})
				}
				if strings.HasPrefix(tc.source, "tcp:") {
					p, err := tcpRequest(m.Config().TCP.Listen, pdu)
					if err != nil {
						t.Fatal(err)
					}
					return p
				}
				return m.Handle(tc.source, 1, pdu)
			}
			state := func() int {
				s := m.Engine.Results()[0]
				if tc.stop {
					return s.Stop.State
				}
				return s.Main.State
			}
			if p := write(value); p[0]&128 != 0 {
				t.Fatal(p)
			}
			until(t, func() bool { return state() == 3 })
			write(0)
			m.Ports[1].Simulator.SetFault(1, fw9000.Fault{DropWrite: true, UnsupportedStart: -1})
			write(value)
			until(t, func() bool { return state() == 5 })
			count := m.Ports[1].Simulator.Count(1)
			before := m.Engine.Results()[0]
			// Broadcast writes cannot clear even a resettable uncertain cycle.
			if p := m.Handle(tc.source, 0, fc06(addr, 0)); p != nil || state() != 5 {
				t.Fatal("broadcast reset")
			}
			p := write(0)
			if p[0]&128 != 0 || state() != 0 || m.Ports[1].Simulator.Count(1) != count {
				t.Fatal("reset failed or sent action", p)
			}
			audit, err := m.Store.Events()
			if err != nil {
				t.Fatal(err)
			}
			b, _ := json.Marshal(before)
			found := false
			for _, event := range audit {
				if event["kind"] != "control_reset" {
					continue
				}
				var record struct {
					Source string          `json:"source"`
					Before json.RawMessage `json:"before"`
				}
				if err := json.Unmarshal([]byte(event["message"].(string)), &record); err != nil {
					t.Fatal(err)
				}
				if !bytes.Equal(record.Before, b) {
					continue
				}
				if tc.source == "rtu:COM3" && record.Source != tc.source || tc.source == "tcp:test" && !strings.HasPrefix(record.Source, "tcp:") {
					t.Fatal("wrong reset source", record.Source)
				}
				found = true
			}
			if !found {
				t.Fatal("original record missing from reset audit")
			}
		})
	}
}

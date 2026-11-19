package command

import (
	"context"
	"encoding/binary"
	"testing"
)

// These literals come from the customer's C01 table, not from the generator.
// Verify the actual FC10 frames rather than only testing the lookup function.
func TestC01ConfirmedCommandsReachCorrectFWAction(t *testing.T) {
	cases := []struct {
		name            string
		command, action uint16
	}{
		{"001 Measure I", 0b001, 0x0001},
		{"010 Measure II interface", 0b010, 0x0010},
		{"011 Measure III tank bottom", 0b011, 0x0020},
		{"100 stop", 0b100, 0x0004},
		{"101 rise", 0b101, 0x0002},
		{"110 lower", 0b110, 0x0100},
		{"111 remote density", 0b111, 0x0040},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			e, _, sim := fixture(t)
			if err := e.Submit(1, false, tc.command, "tcp:c01"); err != nil {
				t.Fatal(err)
			}
			r, ok := e.Pop(1, false)
			if !ok {
				t.Fatal("request was not queued")
			}
			var writes [][2]uint16
			wire := hookedWire{Wire: sim, before: func(p []byte) {
				if len(p) >= 8 && p[0] == 16 {
					if binary.BigEndian.Uint16(p[3:5]) != 1 || p[5] != 2 {
						t.Fatalf("unexpected FC10 frame %x", p)
					}
					writes = append(writes, [2]uint16{binary.BigEndian.Uint16(p[1:3]), binary.BigEndian.Uint16(p[6:8])})
				}
			}}
			e.Execute(context.Background(), r, wire)
			if len(writes) != 2 || writes[0] != [2]uint16{129, 0x10D9} || writes[1] != [2]uint16{699, tc.action} {
				t.Fatalf("wrong south transactions: %v", writes)
			}
			if sim.Count(1) != 1 || e.Results()[0].Main.State != Confirmed {
				t.Fatal("command not confirmed exactly once")
			}
		})
	}
}

func TestC01IndependentStopDoesNotUseRiseCode(t *testing.T) {
	e, _, sim := fixture(t)
	if err := e.Submit(1, true, 1, "rtu:COM4"); err != nil {
		t.Fatal(err)
	}
	r := run(t, e, sim, 1)
	if r.Command != 0b100 || r.Action != 0x0004 || !r.Stop || e.Results()[0].Stop.State != Confirmed {
		t.Fatalf("wrong independent stop: %+v", r)
	}
}

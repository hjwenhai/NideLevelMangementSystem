package command

import (
	"context"
	"encoding/binary"
	"fmt"
	"levelgateway/src/internal/fw9000"
	"levelgateway/src/internal/modbus"
	"testing"
)

func TestActionsWithFaultDiagnostics(t *testing.T) {
	for _, faults := range [][2]uint16{{1, 1}, {0, 2}, {1, 0}} {
		for _, cmd := range []uint16{1, 2, 3, 4, 5, 6, 7, 9, 10, 0} {
			t.Run(fmt.Sprintf("fault_%d_to_%d_cmd%d", faults[0], faults[1], cmd), func(t *testing.T) {
				e, _, sim := fixture(t)
				reads := 0
				writes := [][2]uint16{}
				wire := hookedWire{Wire: sim, before: func(p []byte) {
					if p[0] == 16 {
						writes = append(writes, [2]uint16{binary.BigEndian.Uint16(p[1:]), binary.BigEndian.Uint16(p[6:])})
					}
				}, override: func(p []byte) ([]byte, error, bool) {
					if p[0] != 4 || binary.BigEndian.Uint16(p[1:]) != 51 {
						return nil, nil, false
					}
					if reads >= 2 {
						t.Fatal("unexpected state read")
					}
					fault := faults[reads]
					reads++
					return []byte{4, 4, 0, 1, byte(fault >> 8), byte(fault)}, nil, true
				}}
				stop := cmd == 0
				value, action := cmd, uint16(4)
				if stop {
					value = 1
				} else {
					action, _ = Action(cmd)
				}
				if err := e.Submit(1, stop, value, "fault-test"); err != nil {
					t.Fatal(err)
				}
				run(t, e, wire, 1)
				result := e.Results()[0].Main
				if stop {
					result = e.Results()[0].Stop
				}
				if result.State != Confirmed || result.Error != 0 || !result.Sent || len(result.Readback) != 3 || result.Readback[2] != faults[1] {
					t.Fatal("fault affected confirmation or diagnostic record", result)
				}
				if len(writes) != 2 || writes[0] != [2]uint16{129, 0x10d9} || writes[1] != [2]uint16{699, action} || sim.Count(1) != 1 {
					t.Fatal("wrong authentication/action sequence", writes)
				}
				if err := e.Submit(1, stop, value, "duplicate"); err != nil {
					t.Fatal(err)
				}
				if _, ok := e.Pop(1, false); ok || sim.Count(1) != 1 {
					t.Fatal("duplicate action sent")
				}
			})
		}
	}
}

func TestFaultDoesNotHideControlFailures(t *testing.T) {
	for _, tc := range []struct {
		name    string
		addr    uint16
		failure error
		state   int
		code    uint16
	}{
		{"status_unavailable", 51, context.DeadlineExceeded, Failed, 10},
		{"auth_failed", 129, context.DeadlineExceeded, Failed, 4},
		{"instrument_rejected", 699, modbus.IllegalValue, Failed, 5},
		{"readback_failed", 698, context.DeadlineExceeded, Uncertain, 7},
	} {
		t.Run(tc.name, func(t *testing.T) {
			e, _, sim := fixture(t)
			sim.SetRaw(1, 52, 1)
			wire := hookedWire{Wire: sim, override: func(p []byte) ([]byte, error, bool) {
				if binary.BigEndian.Uint16(p[1:]) == tc.addr {
					return nil, tc.failure, true
				}
				return nil, nil, false
			}}
			if err := e.Submit(1, false, 6, "fault-test"); err != nil {
				t.Fatal(err)
			}
			run(t, e, wire, 1)
			result := e.Results()[0].Main
			if result.State != tc.state || result.Error != tc.code {
				t.Fatal(result)
			}
			want := 0
			if tc.state == Uncertain {
				want = 1
			}
			if sim.Count(1) != want {
				t.Fatal("unexpected action count")
			}
		})
	}
}

func TestFaultAndLostWriteReplyRemainUncertainWhenUnobservable(t *testing.T) {
	e, _, sim := fixture(t)
	sim.SetRaw(1, 52, 1)
	sim.SetFault(1, fw9000.Fault{DropWrite: true, UnsupportedStart: -1})
	wire := hookedWire{Wire: sim, override: func(p []byte) ([]byte, error, bool) {
		// A pre-existing action bit provides no evidence of a new action after a lost reply.
		if p[0] == 3 && binary.BigEndian.Uint16(p[1:]) == 698 {
			return []byte{3, 2, 1, 0}, nil, true
		}
		return nil, nil, false
	}}
	if err := e.Submit(1, false, 6, "fault-test"); err != nil {
		t.Fatal(err)
	}
	run(t, e, wire, 1)
	result := e.Results()[0].Main
	if result.State != Uncertain || result.Error != 6 || sim.Count(1) != 1 {
		t.Fatal(result)
	}
}

func TestLegacyFaultResultSurvivesUpgrade(t *testing.T) {
	e, s, sim := fixture(t)
	if err := e.Submit(1, false, 6, "old-version"); err != nil {
		t.Fatal(err)
	}
	r, ok := e.Pop(1, false)
	if !ok {
		t.Fatal("missing request")
	}
	e.update(r, Failed, 3, "仪表故障，未发送", []uint16{1, 1})
	restored := newTestEngine(s)
	if err := restored.Recover(); err != nil {
		t.Fatal(err)
	}
	result := restored.Results()[0].Main
	if result.State != Failed || result.Error != 3 {
		t.Fatal(result)
	}
	if _, ok := restored.Pop(1, false); ok || sim.Count(1) != 0 {
		t.Fatal("replayed legacy result")
	}
	if err := restored.Submit(1, false, 0, "tcp:manual-confirmed"); err != nil {
		t.Fatal(err)
	}
	if restored.Results()[0].Main.State != Idle || sim.Count(1) != 0 {
		t.Fatal("manual reset sent action")
	}
}

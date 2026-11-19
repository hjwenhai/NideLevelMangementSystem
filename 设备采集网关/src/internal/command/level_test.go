package command

import (
	"context"
	"encoding/binary"
	"levelgateway/src/internal/modbus"
	"testing"
)

func TestRemoteLevelPublishedExampleAndDurableNoReplay(t *testing.T) {
	e, store, sim := fixture(t)
	m := sim.Meters[1]
	m.IR[0] = 2
	m.IR[1] = 0x49f0
	m.HR[503] = 0xffff
	m.HR[504] = 0xec78
	var frames [][]byte
	w := hookedWire{Wire: sim, before: func(p []byte) { frames = append(frames, append([]byte(nil), p...)) }}
	if err := e.SubmitLevel(1, 150100, "web/level-adjust", "工号01", "现场尺量校准"); err != nil {
		t.Fatal(err)
	}
	run(t, e, w, 1)
	c := e.Results()[0].Main
	if c.State != Confirmed || c.Error != 0 || m.HR[503] != 0xffff || m.HR[504] != 0xecdc || m.HR[129] != 0 || m.OffsetWriteCount != 1 || m.ActionCount != 0 || c.Level == nil || !c.Level.PasswordCleared || c.Level.PreviousOffsetRaw != -5000 || c.Request.Operator != "工号01" {
		t.Fatalf("%+v %+v", c, m)
	}
	want := []struct {
		fc byte
		a  uint16
	}{{4, 0}, {3, 503}, {6, 129}, {16, 503}, {3, 503}, {6, 129}}
	if len(frames) != len(want) {
		t.Fatal(frames)
	}
	for i, p := range frames {
		if p[0] != want[i].fc || binary.BigEndian.Uint16(p[1:]) != want[i].a {
			t.Fatalf("frame %d: %x", i, p)
		}
	}
	if binary.BigEndian.Uint16(frames[2][3:]) != 0xea66 || binary.BigEndian.Uint16(frames[5][3:]) != 0 {
		t.Fatal("wrong password")
	}
	for i := 0; i < 3; i++ {
		if err := e.Submit(1, false, 11, "tcp:repeat"); err != nil {
			t.Fatal(err)
		}
	}
	if _, ok := e.Pop(1, false); ok {
		t.Fatal("replayed")
	}
	restored := newTestEngine(store)
	if err := restored.Recover(); err != nil {
		t.Fatal(err)
	}
	if restored.Results()[0].Main.State != Confirmed || restored.Pending(1) != 0 || restored.LevelWords()[2] != 0 {
		t.Fatal("restore")
	}
	if restored.MaintenanceReset(1, false, c.Request.ID, "工号01", "修改结果核查") != nil || m.OffsetWriteCount != 1 || m.HR[504] != 0xecdc {
		t.Fatal("reset wrote instrument")
	}
}
func TestRemoteLevelFailuresCleanupAndNoAutomaticRetry(t *testing.T) {
	for _, name := range []string{"fresh-read", "unsupported-offset", "range", "auth-lost", "write-lost-readable", "write-lost-unreadable", "readback-mismatch", "clear-lost", "explicit-reject", "stop-during-auth", "stop-clear-lost"} {
		t.Run(name, func(t *testing.T) {
			e, _, sim := fixture(t)
			m := sim.Meters[1]
			var offsetWrites, auths, clears, offsetReads int
			target := int32(150100)
			if name == "range" {
				target = 999999
				m.IR[0] = 0xffff
				m.IR[1] = 0xffff
				m.HR[503] = 0
				m.HR[504] = 1
			}
			w := hookedWire{Wire: sim, override: func(p []byte) ([]byte, error, bool) {
				a := binary.BigEndian.Uint16(p[1:])
				fc := p[0]
				if fc == 4 && a == 0 && name == "fresh-read" {
					return nil, context.DeadlineExceeded, true
				}
				if fc == 3 && a == 503 {
					offsetReads++
					if name == "unsupported-offset" {
						return nil, modbus.IllegalAddress, true
					}
					if offsetReads > 1 && name == "write-lost-unreadable" {
						return nil, context.DeadlineExceeded, true
					}
					if offsetReads > 1 && name == "readback-mismatch" {
						return []byte{3, 4, 0, 0, 0, 0}, nil, true
					}
				}
				if fc == 6 && a == 129 && binary.BigEndian.Uint16(p[3:]) == 0xea66 {
					auths++
					if name == "auth-lost" {
						sim.Handle(1, p)
						return nil, context.DeadlineExceeded, true
					}
					if name == "stop-during-auth" || name == "stop-clear-lost" {
						out, err := sim.Exchange(context.Background(), 1, p)
						if e.Submit(1, true, 1, "test-stop") != nil {
							t.Fatal("stop")
						}
						return out, err, true
					}
				}
				if fc == 16 && a == 503 {
					offsetWrites++
					if name == "explicit-reject" {
						return nil, modbus.IllegalValue, true
					}
					if name == "write-lost-readable" || name == "write-lost-unreadable" {
						sim.Handle(1, p)
						return nil, context.DeadlineExceeded, true
					}
				}
				if fc == 6 && a == 129 && binary.BigEndian.Uint16(p[3:]) == 0 {
					clears++
					if name == "clear-lost" || name == "stop-clear-lost" {
						return nil, context.DeadlineExceeded, true
					}
				}
				return nil, nil, false
			}}
			if err := e.SubmitLevel(1, target, "tcp:test", "", ""); err != nil {
				t.Fatal(err)
			}
			run(t, e, w, 1)
			c := e.Results()[0].Main
			expected := Failed
			if name == "write-lost-readable" {
				expected = Confirmed
			}
			if name == "write-lost-unreadable" || name == "readback-mismatch" || name == "clear-lost" || name == "stop-clear-lost" {
				expected = Uncertain
			}
			if c.State != expected {
				t.Fatalf("%s: %+v", name, c)
			}
			beforeAuth := name == "fresh-read" || name == "unsupported-offset" || name == "range"
			if beforeAuth {
				if auths != 0 || clears != 0 || offsetWrites != 0 {
					t.Fatal("preflight wrote")
				}
			} else if auths != 1 || clears != 1 {
				t.Fatalf("auth %d clear %d", auths, clears)
			}
			if offsetWrites > 1 || m.ActionCount != 0 {
				t.Fatal("repeat or HR699")
			}
			if name == "stop-during-auth" && (c.Error != 11 || c.Sent || offsetWrites != 0 || m.HR[129] != 0) {
				t.Fatal(c)
			}
			if name == "stop-clear-lost" && c.Error != 14 {
				t.Fatal("credential uncertainty lost")
			}
			if err := e.Submit(1, false, 11, "tcp:repeat"); err != nil {
				t.Fatal(err)
			}
			if e.Pending(1) > 1 {
				t.Fatal("duplicate queued")
			}
		})
	}
}
func TestRemoteLevelFreshInputsStagingAndRestartBoundaries(t *testing.T) {
	e, store, sim := fixture(t)
	if e.Submit(1, false, 11, "tcp:test") != modbus.IllegalValue {
		t.Fatal("missing target allowed")
	}
	for _, raw := range []int32{-100000, 1000000} {
		if e.StageLevel(1, raw, "tcp:test") != modbus.IllegalValue {
			t.Fatal("range")
		}
	}
	if e.StageLevel(1, 0, "tcp:test") != nil || e.LevelWords()[2] != 1 {
		t.Fatal("zero target")
	}
	recovered := newTestEngine(store)
	if recovered.Recover() != nil || recovered.Submit(1, false, 11, "tcp:test") != modbus.IllegalValue {
		t.Fatal("stale staged target recovered")
	}
	if e.StageLevel(1, 150100, "tcp:test") != nil {
		t.Fatal("stage")
	}
	sim.Meters[1].IR[0] = 2
	sim.Meters[1].IR[1] = 0x49f0
	if e.Submit(1, false, 11, "tcp:test") != nil {
		t.Fatal("submit")
	}
	if e.StageLevel(1, 5, "tcp:other") != modbus.Busy || e.Submit(1, false, 5, "tcp:other") != modbus.Busy {
		t.Fatal("interlock")
	}
	recovered = newTestEngine(store)
	if recovered.Recover() != nil || recovered.Results()[0].Main.State != Uncertain || recovered.Pending(1) != 0 {
		t.Fatal("replayed after outage")
	}
	run(t, e, sim, 1)
	if e.Results()[0].Main.Level.NewOffsetRaw != 100 {
		t.Fatal("did not use fresh input")
	}
}

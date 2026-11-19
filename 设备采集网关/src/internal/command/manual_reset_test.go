package command

import (
	"context"
	"encoding/json"
	"levelgateway/src/internal/modbus"
	"reflect"
	"testing"
)

func TestUncertainRegisterResetAuditIndependenceAndNoReplay(t *testing.T) {
	for _, stop := range []bool{false, true} {
		name := "main"
		if stop {
			name = "stop"
		}
		t.Run(name, func(t *testing.T) {
			e, store, sim := fixture(t)
			if err := e.Submit(1, false, 5, "tcp:original"); err != nil {
				t.Fatal(err)
			}
			oldMain, _ := e.Pop(1, false)
			e.InterruptPort(1)
			if err := e.Submit(1, true, 1, "rtu:COM4"); err != nil {
				t.Fatal(err)
			}
			oldStop, _ := e.Pop(1, false)
			e.InterruptPort(1)
			before := e.Results()[0]
			value := uint16(5)
			if stop {
				value = 1
			}
			if err := e.Submit(1, stop, value, "tcp:duplicate"); err != nil {
				t.Fatal(err)
			}
			if err := e.Submit(1, stop, 0, "tcp:192.168.0.168:12345"); err != nil {
				t.Fatal(err)
			}
			after := e.Results()[0]
			cleared := channel(&after, stop)
			if cleared.State != Idle || cleared.Value != 0 || cleared.Error != 0 || cleared.Count != 1 {
				t.Fatal(after)
			}
			if !reflect.DeepEqual(*channel(&before, !stop), *channel(&after, !stop)) {
				t.Fatal("reset cleared the other channel")
			}
			if !stop && e.Submit(1, false, 5, "new") != modbus.Busy {
				t.Fatal("normal bypassed stop latch")
			}
			events, err := store.Events()
			if err != nil {
				t.Fatal(err)
			}
			var audit struct {
				Source string `json:"source"`
				Time   int64  `json:"reset_time_ms"`
				Stop   bool   `json:"stop"`
				Manual bool   `json:"manual_confirmation_required"`
				Before Slot   `json:"before"`
				After  Slot   `json:"after"`
			}
			if events[0]["kind"] != "control_reset" {
				t.Fatal(events[0])
			}
			if err := json.Unmarshal([]byte(events[0]["message"].(string)), &audit); err != nil {
				t.Fatal(err)
			}
			if audit.Source != "tcp:192.168.0.168:12345" || audit.Time <= 0 || audit.Stop != stop || !audit.Manual || !reflect.DeepEqual(audit.Before, before) || !reflect.DeepEqual(audit.After, after) {
				t.Fatal("incomplete reset audit", audit)
			}
			if err := e.Submit(1, stop, 0, "retry"); err != nil {
				t.Fatal(err)
			}
			repeated, _ := store.Events()
			if len(repeated) != len(events) {
				t.Fatal("idle zero duplicated reset audit")
			}
			// A stale worker and its late result must not execute or overwrite a cleared cycle.
			r := oldMain
			if stop {
				r = oldStop
			}
			e.Execute(context.Background(), r, sim)
			if e.update(r, Confirmed, 0, "late", nil) || sim.Count(1) != 0 {
				t.Fatal("reset replayed old action")
			}
			restarted := newTestEngine(store)
			if err := restarted.Recover(); err != nil {
				t.Fatal(err)
			}
			if !reflect.DeepEqual(restarted.Results()[0], after) {
				t.Fatal("reset not durable")
			}
			if _, ok := restarted.Pop(1, false); ok {
				t.Fatal("restart queued old action")
			}
			if err := restarted.Submit(1, !stop, 0, "rtu:COM4"); err != nil {
				t.Fatal(err)
			}
			if err := restarted.Submit(1, false, 5, "tcp:new-explicit-command"); err != nil {
				t.Fatal(err)
			}
			run(t, restarted, sim, 1)
			if sim.Count(1) != 1 || restarted.Results()[0].Main.Count != 2 {
				t.Fatal("new explicit command failed")
			}
		})
	}
}

func TestUncertainResetStorageFailureKeepsLatch(t *testing.T) {
	e, store, sim := fixture(t)
	e.Submit(1, false, 5, "x")
	e.InterruptPort(1)
	before := e.Results()[0]
	store.Close()
	if e.Submit(1, false, 0, "tcp:manual") != modbus.DeviceFailure || !reflect.DeepEqual(e.Results()[0], before) || sim.Count(1) != 0 {
		t.Fatal("uncommitted reset changed state or sent action")
	}
}

func TestActiveNormalAndStopCyclesStillRejectZero(t *testing.T) {
	for _, stop := range []bool{false, true} {
		for _, running := range []bool{false, true} {
			e, _, sim := fixture(t)
			value := uint16(5)
			if stop {
				value = 1
			}
			if err := e.Submit(1, stop, value, "test"); err != nil {
				t.Fatal(err)
			}
			if running {
				if _, ok := e.Pop(1, false); !ok {
					t.Fatal("no running cycle")
				}
			}
			before := e.Results()[0]
			if e.Submit(1, stop, 0, "tcp:manual") != modbus.Busy || !reflect.DeepEqual(e.Results()[0], before) || sim.Count(1) != 0 {
				t.Fatalf("active reset stop=%t running=%t", stop, running)
			}
		}
	}
}

func TestRegisterLegacyResetIsAtomicAndDoesNotRelock(t *testing.T) {
	e, store, _ := fixture(t)
	store.SaveCommand("old", 6, map[string]any{"request": map[string]any{"slot": 1, "binding_version": 1, "values": []int{2}}, "state": 6})
	if err := e.Recover(); err != nil {
		t.Fatal(err)
	}
	e.Submit(1, true, 1, "stop")
	e.InterruptPort(1)
	if err := e.Submit(1, true, 0, "rtu:COM4"); err != nil {
		t.Fatal(err)
	}
	legacy, err := store.LegacyUnresolved()
	if err != nil || len(legacy) != 1 || e.Results()[0].Main.State != Uncertain {
		t.Fatal("stop reset resolved legacy main", err)
	}
	if err := e.Submit(1, false, 0, "tcp:manual"); err != nil {
		t.Fatal(err)
	}
	legacy, err = store.LegacyUnresolved()
	if err != nil || len(legacy) != 0 {
		t.Fatal("legacy resolution not committed", err)
	}
	restarted := newTestEngine(store)
	if err := restarted.Recover(); err != nil || restarted.Results()[0].Main.State != Idle {
		t.Fatal("legacy record relocked", err)
	}
}

package command

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"levelgateway/src/internal/fw9000"
	"levelgateway/src/internal/modbus"
	"levelgateway/src/internal/platform"
	"levelgateway/src/internal/storage"
	"path/filepath"
	"strings"
	"sync"
	"testing"
)

func fixture(t *testing.T) (*Engine, *storage.Store, *fw9000.Simulator) {
	t.Helper()
	s, err := storage.Open(filepath.Join(t.TempDir(), "db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	e := newTestEngine(s)
	return e, s, fw9000.NewSimulator()
}
func newTestEngine(s *storage.Store) *Engine {
	return New(s, func(n int, c uint16, stop bool, source string) (Request, error) {
		a, ok := Action(c)
		if !ok {
			return Request{}, modbus.IllegalValue
		}
		return Request{Slot: n, Binding: 1, Revision: 1, Port: (n-1)/16 + 1, Unit: byte((n-1)%16 + 1), TimeoutMS: 100, Command: c, Action: a, Stop: stop, Source: source}, nil
	}, func(Request) error { return nil })
}
func run(t *testing.T, e *Engine, sim platform.Wire, port int) Request {
	t.Helper()
	r, ok := e.Pop(port, false)
	if !ok {
		t.Fatal("no queued request")
	}
	e.Execute(context.Background(), r, sim)
	return r
}
func TestD2ActionsAndCycleDedup(t *testing.T) {
	e, _, sim := fixture(t)
	for _, c := range []uint16{1, 2, 3, 4, 5, 6, 7, 9, 10} {
		if err := e.Submit(1, false, c, "tcp:a"); err != nil {
			t.Fatal(err)
		}
		if e.Submit(1, false, 0, "tcp:a") != modbus.Busy {
			t.Fatal("cleared accepted")
		}
		if err := e.Submit(1, false, c, "rtu:COM3"); err != nil {
			t.Fatal(err)
		}
		run(t, e, sim, 1)
		s := e.Results()[0]
		if s.Main.State != Confirmed || s.Main.Value != c || s.Main.Error != 0 {
			t.Fatal(s)
		}
		count := sim.Count(1)
		if err := e.Submit(1, false, c, "tcp:b"); err != nil || sim.Count(1) != count {
			t.Fatal("terminal repeat")
		}
		if e.Submit(1, false, (c%7)+1, "tcp:a") == nil {
			t.Fatal("different command accepted")
		}
		if err := e.Submit(1, false, 0, "tcp:a"); err != nil {
			t.Fatal(err)
		}
	}
	if sim.Count(1) != 9 || e.Results()[0].Main.Count != 9 {
		t.Fatal("action count")
	}
	for _, v := range []uint16{8, 11, 65535} {
		if e.Submit(1, false, v, "x") != modbus.IllegalValue {
			t.Fatal(v)
		}
	}
	if e.Submit(65, false, 5, "x") != modbus.IllegalAddress || e.Submit(1, true, 2, "x") != modbus.IllegalValue {
		t.Fatal("bounds")
	}
	if err := e.Submit(1, false, 5, "x"); err != nil {
		t.Fatal(err)
	}
	run(t, e, sim, 1)
	if sim.Count(1) != 10 {
		t.Fatal("same action after reset swallowed")
	}
}
func TestStopCancelsUnsentAndHasIndependentResult(t *testing.T) {
	e, s, sim := fixture(t)
	if err := e.Submit(1, false, 5, "x"); err != nil {
		t.Fatal(err)
	}
	if err := e.Submit(2, false, 5, "x"); err != nil {
		t.Fatal(err)
	}
	if err := e.Submit(1, true, 1, "x"); err != nil {
		t.Fatal(err)
	}
	v := e.Results()[0]
	if v.Main.State != Failed || v.Main.Error != 11 || v.Main.Value != 5 || v.Stop.State != Accepted {
		t.Fatal(v)
	}
	raw, _ := s.LoadControls()
	var persisted Slot
	json.Unmarshal(raw[1], &persisted)
	if persisted.Main.Error != 11 || persisted.Stop.State != Accepted {
		t.Fatal("stop/cancel not committed together")
	}
	r := run(t, e, sim, 1)
	if !r.Stop || sim.Count(1) != 1 {
		t.Fatal("stop did not get priority")
	}
	if e.Submit(1, false, 0, "x") != nil || e.Submit(1, false, 5, "x") != modbus.Busy {
		t.Fatal("stop latch bypass")
	}
	if e.Submit(1, true, 1, "x") != nil || sim.Count(1) != 1 {
		t.Fatal("stop repeated")
	}
	if e.Submit(1, true, 0, "x") != nil {
		t.Fatal("stop reset")
	}
	run(t, e, sim, 1)
	if sim.Count(2) != 1 || sim.Count(1) != 1 {
		t.Fatal("canceled request resurrected")
	}
}

type hookedWire struct {
	platform.Wire
	before   func([]byte)
	override func([]byte) ([]byte, error, bool)
}

func (w hookedWire) Exchange(ctx context.Context, u byte, p []byte) ([]byte, error) {
	if w.before != nil {
		w.before(p)
	}
	if w.override != nil {
		r, e, ok := w.override(p)
		if ok {
			return r, e
		}
	}
	return w.Wire.Exchange(ctx, u, p)
}
func TestStopRaceDuringAuthenticationAndAfterClaim(t *testing.T) {
	for _, sent := range []bool{false, true} {
		t.Run(map[bool]string{false: "auth", true: "sent"}[sent], func(t *testing.T) {
			e, _, sim := fixture(t)
			e.Submit(1, false, 5, "x")
			r, _ := e.Pop(1, false)
			once := sync.Once{}
			w := hookedWire{Wire: sim, before: func(p []byte) {
				if p[0] == 16 && binary.BigEndian.Uint16(p[1:]) == map[bool]uint16{false: 129, true: 699}[sent] {
					once.Do(func() {
						if err := e.Submit(1, true, 1, "stop"); err != nil {
							t.Fatal(err)
						}
					})
				}
			}}
			e.Execute(context.Background(), r, w)
			v := e.Results()[0]
			if !sent && (v.Main.Error != 11 || sim.Count(1) != 0) {
				t.Fatal("auth race sent canceled action", v)
			}
			if sent && (v.Main.State != Confirmed || sim.Count(1) != 1) {
				t.Fatal("already sent action overwritten", v)
			}
			run(t, e, sim, 1)
			if e.Results()[0].Stop.State != Confirmed {
				t.Fatal("stop failed")
			}
		})
	}
}
func TestLostReplyNeverRepeatsAndWebManualReview(t *testing.T) {
	e, s, sim := fixture(t)
	sim.SetFault(1, fw9000.Fault{DropWrite: true, UnsupportedStart: -1})
	e.Submit(1, false, 5, "x")
	run(t, e, sim, 1)
	if e.Results()[0].Main.State != Confirmed {
		t.Fatal("changed readback didn't confirm")
	}
	e.Submit(1, false, 0, "x")
	e.Submit(1, false, 5, "x")
	run(t, e, sim, 1)
	v := e.Results()[0]
	if v.Main.State != Uncertain || v.Main.Error != 6 || sim.Count(1) != 2 {
		t.Fatal(v)
	}
	if e.Submit(1, false, 5, "x") != nil || sim.Count(1) != 2 {
		t.Fatal("unknown replay")
	}
	if e.MaintenanceReset(1, false, "wrong", "工号01", "现场核查动作已停止") != modbus.IllegalValue {
		t.Fatal("stale maintenance accepted")
	}
	if err := e.MaintenanceReset(1, false, v.Main.Request.ID, "工号01", "现场核查动作已停止"); err != nil {
		t.Fatal(err)
	}
	events, _ := s.Events()
	b, _ := json.Marshal(events)
	if !strings.Contains(string(b), "工号01") || !strings.Contains(string(b), v.Main.Request.ID) {
		t.Fatal("missing identity/cycle audit")
	}
}
func TestFreshPreflightFaultAuthRejectAndReadback(t *testing.T) {
	cases := []struct {
		name        string
		state, code int
		mutate      func(*fw9000.Simulator)
		addr        uint16
		failure     error
	}{
		{"local_fault", 3, 0, func(s *fw9000.Simulator) { s.SetRaw(1, 51, 1); s.SetRaw(1, 52, 1) }, 0, nil},
		{"fault", 3, 0, func(s *fw9000.Simulator) { s.SetRaw(1, 52, 1) }, 0, nil},
		{"auth", 4, 4, nil, 129, context.DeadlineExceeded},
		{"reject", 4, 5, nil, 699, modbus.IllegalValue},
		{"readback", 5, 7, nil, 698, context.DeadlineExceeded},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			e, _, sim := fixture(t)
			if tc.mutate != nil {
				tc.mutate(sim)
			}
			e.Submit(1, false, 5, "x")
			w := hookedWire{Wire: sim, override: func(p []byte) ([]byte, error, bool) {
				if tc.failure != nil && binary.BigEndian.Uint16(p[1:]) == tc.addr {
					return nil, tc.failure, true
				}
				return nil, nil, false
			}}
			run(t, e, w, 1)
			c := e.Results()[0].Main
			if c.State != tc.state || int(c.Error) != tc.code {
				t.Fatal(c)
			}
			if tc.state == 4 && sim.Count(1) != 0 {
				t.Fatal("failed preflight sent action")
			}
		})
	}
}
func TestRestartPreservesBothLatchesNoReplay(t *testing.T) {
	e, s, sim := fixture(t)
	e.Submit(1, false, 5, "x")
	run(t, e, sim, 1)
	e.Submit(2, false, 5, "x")
	e.Submit(3, true, 1, "x")
	restarted := newTestEngine(s)
	if err := restarted.Recover(); err != nil {
		t.Fatal(err)
	}
	v := restarted.Results()
	if v[0].Main.State != 3 || v[0].Main.Value != 5 || v[1].Main.State != 5 || v[1].Main.Error != 9 || v[1].Main.Value != 5 || v[2].Stop.State != 5 || v[2].Stop.Error != 9 || v[2].Stop.Value != 1 {
		t.Fatal(v[:3])
	}
	if _, ok := restarted.Pop(1, false); ok {
		t.Fatal("replayed on restart")
	}
	// Independent stop is still accepted for the unknown normal cycle.
	if err := restarted.Submit(2, true, 1, "x"); err != nil {
		t.Fatal(err)
	}
	run(t, restarted, sim, 1)
	if restarted.Results()[1].Main.State != 5 || restarted.Results()[1].Stop.State != 3 {
		t.Fatal("stop erased unknown")
	}
	if err := restarted.Submit(2, false, 0, "tcp:manual-confirmed"); err != nil || restarted.Results()[1].Main.State != Idle || restarted.Results()[1].Stop.State != Confirmed {
		t.Fatal("recovered unknown manual reset failed", err)
	}
}
func TestStorageFailuresFailClosedBeforeAndAfterAcceptance(t *testing.T) {
	t.Run("before", func(t *testing.T) {
		e, s, sim := fixture(t)
		s.Close()
		if e.Submit(1, false, 5, "x") != modbus.DeviceFailure || e.Results()[0].Main.State != 0 || sim.Count(1) != 0 {
			t.Fatal("acted before commit")
		}
	})
	t.Run("after", func(t *testing.T) {
		e, s, sim := fixture(t)
		e.Submit(1, false, 5, "x")
		s.Close()
		if _, ok := e.Pop(1, false); ok {
			t.Fatal("executed without running audit")
		}
		if e.Results()[0].Main.State != 5 || e.Results()[0].Main.Error != 8 || e.Status() == "" || e.Submit(2, true, 1, "x") != modbus.DeviceFailure || sim.Count(1) != 0 {
			t.Fatal("not fail closed")
		}
	})
	t.Run("restore", func(t *testing.T) {
		e, s, _ := fixture(t)
		s.Close()
		if e.Recover() == nil {
			t.Fatal("restore unexpectedly passed")
		}
		if _, err := e.Words(); err != modbus.DeviceFailure {
			t.Fatal("fake zero controls")
		}
	})
}
func TestConcurrentClientsOneCycleAndSaturatingCounters(t *testing.T) {
	e, _, sim := fixture(t)
	var wg sync.WaitGroup
	for i := 0; i < 30; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			if err := e.Submit(1, false, 5, "tcp"); err != nil {
				t.Error(err)
			}
		}()
	}
	wg.Wait()
	if e.Pending(1) != 1 || e.Results()[0].Main.Count != 1 {
		t.Fatal("duplicates queued")
	}
	run(t, e, sim, 1)
	if sim.Count(1) != 1 {
		t.Fatal("duplicate sends")
	}
	e.Submit(1, false, 0, "x")
	e.mu.Lock()
	e.slots[0].Main.Count = ^uint32(0)
	e.mu.Unlock()
	e.Submit(1, false, 5, "x")
	if e.Results()[0].Main.Count != ^uint32(0) {
		t.Fatal("counter wrapped")
	}
}
func TestLegacyMigrationResolutionDoesNotRelock(t *testing.T) {
	e, s, sim := fixture(t)
	old := map[string]any{"request": map[string]any{"slot": 1, "binding_version": 1, "values": []int{2}}, "state": 6}
	s.SaveCommand("old", 6, old)
	if err := e.Recover(); err != nil {
		t.Fatal(err)
	}
	c := e.Results()[0].Main
	if c.State != 5 || c.Value != 5 {
		t.Fatal(c)
	}
	if err := e.MaintenanceReset(1, false, c.Request.ID, "厂家01", "已核查旧仪表停止，记录人工验收"); err != nil {
		t.Fatal(err)
	}
	e.Submit(1, false, 5, "x")
	run(t, e, sim, 1)
	e.Submit(1, false, 0, "x")
	r := newTestEngine(s)
	if err := r.Recover(); err != nil {
		t.Fatal(err)
	}
	if r.Results()[0].Main.State != 0 {
		t.Fatal("resolved legacy relocked")
	}
}
func TestCanceledOldWorkerCannotOverwriteNewCycle(t *testing.T) {
	e, _, sim := fixture(t)
	e.Submit(1, false, 5, "x")
	old, _ := e.Pop(1, false)
	e.Submit(1, true, 1, "x")
	run(t, e, sim, 1)
	e.Submit(1, true, 0, "x")
	e.Submit(1, false, 0, "x")
	e.Submit(1, false, 6, "x")
	e.Execute(context.Background(), old, sim)
	if e.Results()[0].Main.State != 1 || sim.Count(1) != 1 {
		t.Fatal("old worker corrupted new request")
	}
	run(t, e, sim, 1)
	if e.Results()[0].Main.State != 3 || sim.Count(1) != 2 {
		t.Fatal("new cycle failed")
	}
}

func TestCorruptRestoreFailsClosed(t *testing.T) {
	e, s, _ := fixture(t)
	if err := s.SaveControls([]storage.ControlChange{{Slot: 1, Data: map[string]any{"slot": 1, "main": map[string]any{"state": 3, "value": 5}, "stop": map[string]any{"state": 0}}}}, "fixture", "corrupt"); err != nil {
		t.Fatal(err)
	}
	if e.Recover() == nil {
		t.Fatal("corrupt terminal accepted")
	}
	if _, err := e.Words(); err != modbus.DeviceFailure {
		t.Fatal("corrupt restore published empty control")
	}
}
func TestStopMaintenanceDoesNotResolveLegacyNormalAndCannotChangeTarget(t *testing.T) {
	e, s, sim := fixture(t)
	s.SaveCommand("old", 6, map[string]any{"request": map[string]any{"slot": 1, "binding_version": 1, "values": []int{2}}, "state": 6})
	if err := e.Recover(); err != nil {
		t.Fatal(err)
	}
	e.Submit(1, true, 1, "stop")
	r, ok := e.Pop(1, false)
	if !ok {
		t.Fatal("stop not queued")
	}
	e.update(r, Uncertain, 9, "fixture interruption", nil)
	c := e.Results()[0].Stop
	if err := e.MaintenanceReset(1, true, c.Request.ID, "SIM-TEST", "只核查独立停止，不处理旧正常周期"); err != nil {
		t.Fatal(err)
	}
	legacy, err := s.LegacyUnresolved()
	if err != nil || len(legacy) != 1 || e.Results()[0].Main.State != 5 {
		t.Fatal("stop maintenance resolved legacy normal")
	}
	oldFreeze := e.freeze
	e.freeze = func(n int, c uint16, stop bool, source string) (Request, error) {
		r, err := oldFreeze(n, c, stop, source)
		r.Binding = 2
		return r, err
	}
	if e.Submit(1, true, 1, "stop-new-target") != modbus.IllegalAddress || sim.Count(1) != 0 {
		t.Fatal("unknown cycle allowed stop on changed target")
	}
}

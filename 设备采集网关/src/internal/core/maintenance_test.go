package core

import (
	"fmt"
	"levelgateway/src/internal/clockstate"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/storage"
	"path/filepath"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func restartFixture(t *testing.T) *Manager {
	t.Helper()
	s, err := storage.Open(filepath.Join(t.TempDir(), "db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { s.Close() })
	m := &Manager{Store: s, cfg: config.Default(1), Boot: 1, Started: time.Now(), Simulate: true}
	m.clock = clockstate.New("", "", true, clockstate.Ops{Now: time.Now})
	m.Engine = command.New(s, func(n int, v uint16, stop bool, source string) (command.Request, error) {
		action, _ := command.Action(v)
		return command.Request{Slot: n, Binding: 1, Revision: 1, Port: 1, Unit: 1, TimeoutMS: 500, Command: v, Action: action, Stop: stop, Source: source}, nil
	}, nil)
	return m
}
func TestProgramRestartActiveControlAndPendingGate(t *testing.T) {
	for _, running := range []bool{false, true} {
		t.Run(fmt.Sprint(running), func(t *testing.T) {
			m := restartFixture(t)
			m.ConfigureProgramRestart(func() (func() error, error) { t.Fatal("active control launched restart"); return nil, nil })
			if err := m.Engine.Submit(1, false, 5, "test"); err != nil {
				t.Fatal(err)
			}
			if running {
				m.Engine.Pop(1, false)
			}
			if _, err := m.RequestProgramRestart("web:test"); err == nil {
				t.Fatal("active control accepted restart")
			}
		})
	}
	m := restartFixture(t)
	done := make(chan struct{})
	defer close(done)
	var launches atomic.Int32
	m.ConfigureProgramRestart(func() (func() error, error) {
		launches.Add(1)
		return func() error { <-done; return fmt.Errorf("script failure") }, nil
	})
	first, err := m.RequestProgramRestart("web:test")
	if err != nil || !first.Pending {
		t.Fatal(first, err)
	}
	second, err := m.RequestProgramRestart("web:test")
	if err != nil || second.ID != first.ID || launches.Load() != 1 {
		t.Fatal("duplicate restart launched")
	}
	if err = m.Apply(m.Config()); err == nil {
		t.Fatal("config during restart accepted")
	}
	for _, addr := range []uint16{30000, 30007} {
		pdu := []byte{6, byte(addr >> 8), byte(addr), 0, 0}
		out := m.Handle("tcp:test", 1, pdu)
		if len(out) != 2 || out[1] != 6 {
			t.Fatal("write/reset not busy", out)
		}
	}
}
func TestProgramRestartPreservesUnknownAndLaunchFailure(t *testing.T) {
	m := restartFixture(t)
	request := command.Request{ID: strings.Repeat("a", 32), Slot: 1, Binding: 1, Revision: 1, Port: 1, Unit: 1, TimeoutMS: 500, Command: 5, Action: 2, Source: "test"}
	slot := command.Slot{Number: 1, Main: command.Cycle{Value: 5, State: 5, Error: 9, Count: 1, Request: request, Sent: true}}
	if err := m.Store.SaveControls([]storage.ControlChange{{Slot: 1, Data: slot}}, "fixture", "unknown"); err != nil {
		t.Fatal(err)
	}
	if err := m.Engine.Recover(); err != nil {
		t.Fatal(err)
	}
	before := m.Engine.Results()
	done := make(chan struct{})
	defer close(done)
	m.ConfigureProgramRestart(func() (func() error, error) { return func() error { <-done; return nil }, nil })
	if _, err := m.RequestProgramRestart("web:test"); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(before, m.Engine.Results()) {
		t.Fatal("restart reset or replayed unknown")
	}
	other := restartFixture(t)
	other.ConfigureProgramRestart(func() (func() error, error) { return nil, fmt.Errorf("spawn error") })
	if _, err := other.RequestProgramRestart("web:test"); err == nil || other.ProgramRestartStatus().Pending {
		t.Fatal("failed spawn left restart gate")
	}
	other.ConfigureProgramRestart(func() (func() error, error) { return func() error { return fmt.Errorf("worker failed") }, nil })
	if _, err := other.RequestProgramRestart("web:test"); err != nil {
		t.Fatal(err)
	}
	until(t, func() bool { return !other.ProgramRestartStatus().Pending })
	if other.ProgramRestartStatus().Error == "" {
		t.Fatal("worker failure invisible")
	}
}

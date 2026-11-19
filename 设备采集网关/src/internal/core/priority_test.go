package core

import (
	"context"
	"encoding/binary"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/fw9000"
	"levelgateway/src/internal/platform"
	"levelgateway/src/internal/storage"
	"path/filepath"
	"testing"
)

type injectStopWire struct {
	platform.Wire
	engine *command.Engine
	at     uint16
	seen   bool
	order  []uint16
}

func (w *injectStopWire) Exchange(ctx context.Context, u byte, p []byte) ([]byte, error) {
	a := binary.BigEndian.Uint16(p[1:])
	out, err := w.Wire.Exchange(ctx, u, p)
	if p[0] == 16 && a == 699 {
		w.order = append(w.order, binary.BigEndian.Uint16(p[6:]))
	}
	if !w.seen && a == w.at {
		w.seen = true
		if e := w.engine.Submit(1, true, 1, "stop-at-frame"); e != nil {
			panic(e)
		}
	}
	return out, err
}
func TestManagerStopPriorityAtFrameBoundary(t *testing.T) {
	for _, at := range []uint16{129, 699} {
		t.Run(map[uint16]string{129: "auth unsent", 699: "after send"}[at], func(t *testing.T) {
			s, e := storage.Open(filepath.Join(t.TempDir(), "db"))
			if e != nil {
				t.Fatal(e)
			}
			defer s.Close()
			engine := command.New(s, func(n int, c uint16, stop bool, source string) (command.Request, error) {
				a, _ := command.Action(c)
				return command.Request{Slot: n, Binding: 1, Revision: 1, Port: 1, Unit: 1, TimeoutMS: 100, Command: c, Action: a, Stop: stop, Source: source}, nil
			}, func(command.Request) error { return nil })
			sim := fw9000.NewSimulator()
			wire := &injectStopWire{Wire: sim, engine: engine, at: at}
			m := &Manager{Engine: engine}
			rt := &Runtime{Port: config.Port{ID: 1}, Wire: wire}
			engine.Submit(1, false, 5, "normal")
			r, ok := engine.Pop(1, false)
			if !ok {
				t.Fatal("not queued")
			}
			m.executeCommand(context.Background(), rt, r)
			v := engine.Results()[0]
			if v.Stop.State != 3 {
				t.Fatal("stop was not processed at boundary", v)
			}
			if at == 129 && (v.Main.State != 4 || v.Main.Error != 11 || len(wire.order) != 1 || wire.order[0] != 4) {
				t.Fatal("unsent normal wasn't canceled", v, wire.order)
			}
			if at == 699 && (v.Main.State != 5 || len(wire.order) != 2 || wire.order[0] != 2 || wire.order[1] != 4) {
				t.Fatal("sent result fabricated", v, wire.order)
			}
		})
	}
}

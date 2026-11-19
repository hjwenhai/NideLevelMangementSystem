package core

import (
	"context"
	"levelgateway/src/internal/command"
	"levelgateway/src/internal/platform"
	"time"
)

// A pending stop takes the bus at the first completed frame, including frames
// inside normal-command authentication/readback. It never interrupts an RTU frame.
type priorityWire struct {
	platform.Wire
	after func()
}

func (w priorityWire) Exchange(ctx context.Context, u byte, p []byte) ([]byte, error) {
	out, err := w.Wire.Exchange(ctx, u, p)
	w.after()
	return out, err
}
func (m *Manager) executeCommand(ctx context.Context, rt *Runtime, r command.Request) {
	var w platform.Wire = rt.Wire
	if !r.Stop {
		w = priorityWire{Wire: rt.Wire, after: func() {
			if ctx.Err() == nil {
				if stop, ok := m.Engine.Pop(rt.Port.ID, true); ok {
					m.Engine.Execute(ctx, stop, rt.Wire)
				}
			}
		}}
	}
	m.Engine.Execute(ctx, r, w)
	if r.Command == command.LevelAdjustCommand {
		cc, cancel := context.WithTimeout(ctx, time.Duration(r.TimeoutMS)*time.Millisecond)
		words, err := platform.ReadRegisters(cc, w, r.Unit, 4, 0, 6)
		cancel()
		if err == nil {
			m.Cache.Update(r.Slot, 0, words, m.TimeTrusted.Load())
		} else {
			m.Cache.InvalidateLevel(r.Slot, err)
		}
	}
}

package core

import (
	"context"
	"encoding/binary"
	"levelgateway/src/internal/platform"
	"sync"
	"testing"
	"time"
)

type delayedCaptureWire struct {
	platform.Wire
	mu   sync.Mutex
	seen map[uint16]int
}

func (w *delayedCaptureWire) Exchange(ctx context.Context, unit byte, p []byte) ([]byte, error) {
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-time.After(25 * time.Millisecond):
	}
	if unit == 1 {
		w.mu.Lock()
		w.seen[binary.BigEndian.Uint16(p[1:])]++
		w.mu.Unlock()
	}
	return w.Wire.Exchange(ctx, unit, p)
}

func TestSaturated16MeterBusStillServicesDetailsAndParameters(t *testing.T) {
	m := testManager(t, 16)
	rt := m.Ports[1]
	m.stop(rt)
	wire := &delayedCaptureWire{Wire: rt.Simulator, seen: map[uint16]int{}}
	rt.Wire = wire
	rt.mu.Lock()
	rt.closing = false
	rt.mu.Unlock()
	rt.done = make(chan struct{})
	rt.Meters[0].Analog = true
	m.start(rt, m.Config())
	// 16 * 3 * 25ms exceeds the 1000ms target, so there is no fast-idle gap.
	deadline := time.Now().Add(9 * time.Second)
	for time.Now().Before(deadline) {
		wire.mu.Lock()
		ok := wire.seen[6] > 0 && wire.seen[33] > 0 && wire.seen[27] > 0 && wire.seen[3300] > 0
		wire.mu.Unlock()
		if ok {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	wire.mu.Lock()
	defer wire.mu.Unlock()
	t.Fatal("slow groups starved on saturated bus", wire.seen)
}

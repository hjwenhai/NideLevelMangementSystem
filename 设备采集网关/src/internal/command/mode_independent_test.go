package command

import (
	"encoding/binary"
	"fmt"
	"testing"
)

// Supply actual local/remote words on both fresh reads; the simulator must not
// silently set the remote bit after HR699 and hide a readback dependency.
func TestControlDoesNotRequireRemoteBit(t *testing.T) {
	for _, modes := range [][2]uint16{{0, 0}, {1, 1}, {0x8000, 0x8000}, {0x8001, 0x8001}, {0x8000, 1}, {1, 0x8000}} {
		for _, cmd := range []uint16{1, 2, 3, 4, 5, 6, 7, 9, 10, 0} {
			t.Run(fmt.Sprintf("%04x_to_%04x_cmd%d", modes[0], modes[1], cmd), func(t *testing.T) {
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
						t.Fatal("unexpected extra state read")
					}
					v := modes[reads]
					reads++
					return []byte{4, 4, byte(v >> 8), byte(v), 0, 0}, nil, true
				}}
				stop := cmd == 0
				value, action := cmd, uint16(4)
				if stop {
					value = 1
				} else {
					action, _ = Action(cmd)
				}
				if err := e.Submit(1, stop, value, "mode-test"); err != nil {
					t.Fatal(err)
				}
				run(t, e, wire, 1)
				result := e.Results()[0].Main
				if stop {
					result = e.Results()[0].Stop
				}
				if result.State != Confirmed || result.Error != 0 || !result.Sent || reads != 2 || len(result.Readback) != 3 || result.Readback[1] != modes[1] {
					t.Fatal("mode incorrectly blocked dispatch/confirmation or raw mode changed", result, reads)
				}
				if len(writes) != 2 || writes[0] != [2]uint16{129, 0x10d9} || writes[1] != [2]uint16{699, action} || sim.Count(1) != 1 {
					t.Fatal("expected authentication then exactly one action", writes)
				}
				if err := e.Submit(1, stop, value, "duplicate"); err != nil {
					t.Fatal(err)
				}
				if _, ok := e.Pop(1, false); ok || sim.Count(1) != 1 || len(writes) != 2 {
					t.Fatal("duplicate queued/sent")
				}
			})
		}
	}
}

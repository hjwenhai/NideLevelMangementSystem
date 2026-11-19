package command

import (
	"encoding/binary"
	"testing"
)

func TestEveryFloatOperationAuthenticatesBeforeAction(t *testing.T) {
	e, _, sim := fixture(t)
	writes := [][2]uint16{}
	wire := hookedWire{Wire: sim, before: func(p []byte) {
		if p[0] == 16 {
			if binary.BigEndian.Uint16(p[3:]) != 1 || p[5] != 2 {
				t.Fatal("unexpected write shape", p)
			}
			writes = append(writes, [2]uint16{binary.BigEndian.Uint16(p[1:]), binary.BigEndian.Uint16(p[6:])})
		}
	}}
	for _, cmd := range []uint16{1, 2, 3, 4, 5, 6, 7, 9, 10} {
		before := len(writes)
		if err := e.Submit(1, false, cmd, "auth-test"); err != nil {
			t.Fatal(err)
		}
		run(t, e, wire, 1)
		action, _ := Action(cmd)
		if len(writes) != before+2 || writes[before] != [2]uint16{129, 0x10d9} || writes[before+1] != [2]uint16{699, action} {
			t.Fatal("authentication order/value", cmd, writes)
		}
		e.Submit(1, false, cmd, "duplicate")
		if len(writes) != before+2 {
			t.Fatal("duplicate resent")
		}
		e.Submit(1, false, 0, "reset")
	}
	if err := e.Submit(1, true, 1, "independent-stop"); err != nil {
		t.Fatal(err)
	}
	run(t, e, wire, 1)
	n := len(writes)
	if writes[n-2] != [2]uint16{129, 0x10d9} || writes[n-1] != [2]uint16{699, 4} {
		t.Fatal("stop missing auth", writes)
	}
}

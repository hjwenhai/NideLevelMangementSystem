package core

import (
	"levelgateway/src/internal/modbus"
	"testing"
)

func TestCapacity64ExtendedBatchesAndLastControl(t *testing.T) {
	m := testManager(t, 64)
	until(t, func() bool {
		for _, v := range m.Views() {
			if v.Valid != 127 {
				return false
			}
		}
		return true
	})
	for _, fc := range []byte{3, 4} {
		for bank := 0; bank < 2; bank++ {
			a, err := m.Registers(fc, uint16(15000+bank*200), 96)
			if err != nil {
				t.Fatal(err)
			}
			b, err := m.Registers(fc, uint16(15100+bank*200), 96)
			if err != nil {
				t.Fatal(err)
			}
			for i := 0; i < 32; i++ {
				slot := bank*32 + i
				v, err := m.Registers(fc, uint16(1000+24*slot), 24)
				if err != nil || modbus.Get32(a[i*3:]) != modbus.Get32(v[4:]) || a[i*3+2] != v[6] || b[i*3] != v[7] || b[i*3+2] != 127 {
					t.Fatal("batch/summary mismatch", slot+1, err)
				}
			}
		}
	}
	for _, slot := range []int{16, 17, 32, 33, 48, 49, 64} {
		v, e := m.Registers(4, uint16(1000+24*(slot-1)), 24)
		if e != nil || v[18] != uint16((slot-1)/16+1) || v[19] != uint16((slot-1)%16+1) {
			t.Fatal("binding", slot, v, e)
		}
	}
	if out := m.Handle("tcp:capacity", 1, fc06(30756, 5)); len(out) != 5 || out[0] != 6 {
		t.Fatal("last slot rejected", out)
	}
	until(t, func() bool { return m.Engine.Results()[63].Main.State == 3 })
	w, e := m.Registers(3, 30756, 12)
	if e != nil || w[0] != 5 || w[1] != 3 || w[5] != 1 {
		t.Fatal("slot64 control", w, e)
	}
	if _, e = m.Registers(4, 15295, 2); e != modbus.IllegalAddress {
		t.Fatal("extended batch overread")
	}
	if _, e = m.Registers(4, 14000, 128); e != modbus.IllegalValue {
		t.Fatal("125 word bound bypass")
	}
}

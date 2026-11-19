package core

import (
	"levelgateway/src/internal/cache"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"testing"
	"time"
)

func TestV3PreservedBatchAndCapacityBoundaries(t *testing.T) {
	m := testManager(t, 32)
	until(t, func() bool { return m.Views()[31].Valid == 127 })
	device, e := m.Registers(4, 0, 28)
	if e != nil || device[2] != 3 || device[3] != 64 || device[27] != 16 {
		t.Fatal("V3 discovery", device, e)
	}
	for _, fc := range []byte{3, 4} {
		p, e := tcpRequest(m.Config().TCP.Listen, modbus.ReadPDU(fc, 15000, 96))
		if e != nil {
			t.Fatal(e)
		}
		w, e := modbus.DecodeWords(p, 96)
		if e != nil {
			t.Fatal(e)
		}
		s, e := m.Registers(fc, 15100, 96)
		if e != nil {
			t.Fatal(e)
		}
		for i := 0; i < 32; i++ {
			address := i%16 + 1
			if modbus.Get32(w[i*3:]) != uint32(100000+address*123) || w[i*3+2] != uint16(2000+address) || s[i*3] != 8510 || s[i*3+2] != 127 {
				t.Fatalf("batch slot%d: %v %v", i+1, w[i*3:i*3+3], s[i*3:i*3+3])
			}
		}
	}
	for _, start := range []uint16{2536, 12216, 14128, 14264, 14364, 14464, 14564, 14664, 23072, 15096, 15196, 15296, 15396} {
		if _, e := m.Registers(4, start, 1); e != modbus.IllegalAddress {
			t.Fatalf("removed/unallocated address%d accepted", start)
		}
	}
	if _, e := m.Registers(4, 15095, 2); e != modbus.IllegalAddress {
		t.Fatal("batch boundary crossed")
	}
	if reply := m.Handle("tcp:test", 1, []byte{6, 0x76, 0xb0, 0, 5}); reply[1] != 2 {
		t.Fatal("slot33 accepted")
	}

}

func TestIndicatorSemantics(t *testing.T) {
	good := cache.View{Meter: cache.Meter{Config: config.Meter{Enabled: true}}, Quality: 3, Valid: 127}
	bad := good
	bad.Quality = 3 | 256
	cases := []struct {
		name                                    string
		views                                   []cache.View
		maintenance, healthy, trusted, critical bool
		want                                    [3]int
	}{
		{"healthy", []cache.View{good}, false, true, true, false, [3]int{4, 1, 0}},
		{"partial", []cache.View{good, bad}, false, true, true, false, [3]int{4, 2, 2}},
		{"none fresh", []cache.View{bad}, false, true, true, false, [3]int{4, 3, 2}},
		{"maintenance", []cache.View{good}, true, true, true, false, [3]int{4, 0, 0}},
		{"time", []cache.View{good}, false, true, false, false, [3]int{4, 1, 2}},
		{"storage or command", []cache.View{good}, false, true, true, true, [3]int{4, 1, 1}},
		{"scheduler", []cache.View{good}, false, false, true, false, [3]int{1, 1, 1}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			if got := indicatorModes(c.views, c.maintenance, c.healthy, c.trusted, c.critical); got != c.want {
				t.Fatal(got, c.want)
			}
		})
	}
	if !IndicatorOn(4, 0) || IndicatorOn(4, 500*time.Millisecond) || !IndicatorOn(2, 900*time.Millisecond) || IndicatorOn(2, 1100*time.Millisecond) || IndicatorOn(0, 0) || !IndicatorOn(1, 9*time.Second) {
		t.Fatal("blink timing")
	}
	m := testManager(t, 1)
	m.RunIndicators(m.ctx)
	if m.IndicatorStatus().Enabled {
		t.Fatal("simulation accessed physical LEDs")
	}
}

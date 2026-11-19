package cache

import (
	"context"
	"errors"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"testing"
	"time"
)

func TestRemoteLevelInvalidatesPreAdjustmentCache(t *testing.T) {
	c := New()
	c.Bind(config.Default(1).Meters[0])
	c.Update(1, 0, []uint16{0, 100, 0, 0, 0, 0}, true)
	if c.Snapshot(time.Now(), 3000, true)[0].Valid&1 == 0 {
		t.Fatal("fixture")
	}
	c.InvalidateLevel(1, errors.New("post adjustment read failed"))
	v := c.Snapshot(time.Now(), 3000, true)[0]
	if v.Valid&0x41 != 0 || v.Quality&0x104 == 0 {
		t.Fatal("old level still valid", v)
	}
	c.Update(1, 0, []uint16{0, 200, 0, 0, 0, 0}, true)
	v = c.Snapshot(time.Now(), 3000, true)[0]
	if v.Valid&1 == 0 || v.LevelMM != 20 {
		t.Fatal("fresh level not recovered")
	}
}

func TestSignedQualityAndAge(t *testing.T) {
	c := New()
	c.Bind(config.Default(1).Meters[0])
	level := int32(-12345)
	temperature := int16(-1250)
	w := make([]uint16, 6)
	modbus.Put32(w, uint32(level))
	c.Update(1, 0, w)
	c.Update(1, 1, []uint16{0, 0, 0, 0, uint16(temperature)})
	c.Update(1, 2, []uint16{8500, 8510, 0x8000, 0})
	c.Complete(1)
	v := c.Snapshot(time.Now(), 1000, true)[0]
	if v.LevelMM != -1234.5 || v.TemperatureC != -12.5 || v.DensityKgM3 != 851 || v.Valid != 127 {
		t.Fatalf("%+v", v)
	}
	ts := v.FastCompleteMS
	c.Fail(1, 0, context.DeadlineExceeded, false)
	v = c.Snapshot(time.Now().Add(4*time.Second), 1000, true)[0]
	if v.Valid != 0 || v.Quality&4 == 0 || v.FastCompleteMS != ts {
		t.Fatal("old data was made fresh")
	}
	c.SetConfig(config.Meter{Slot: 1, Port: 2, Address: 1, Binding: 2, Enabled: true})
	v = c.Snapshot(time.Now(), 1000, true)[0]
	if v.Valid != 0 || v.Raw[0] != 0 || v.Config.Binding != 2 {
		t.Fatal("old target value leaked")
	}
}
func TestClockTrustDoesNotChangeOldSample(t *testing.T) {
	c := New()
	c.Bind(config.Default(1).Meters[0])
	for i, n := range []int{6, 5, 4} {
		c.Update(1, i, make([]uint16, n), false)
	}
	if v := c.Snapshot(time.Now(), 1000, true)[0]; v.Quality&128 == 0 || c.Summary(v, true)[22] != 0 {
		t.Fatal("old sample falsely trusted")
	}
	for i, n := range []int{6, 5, 4} {
		c.Update(1, i, make([]uint16, n), true)
	}
	if v := c.Snapshot(time.Now(), 1000, true)[0]; v.Quality&128 != 0 {
		t.Fatal("new sample not trusted")
	}
	c.InvalidateTimeQuality()
	if c.Snapshot(time.Now(), 1000, true)[0].Quality&128 == 0 {
		t.Fatal("jump did not invalidate time")
	}
}

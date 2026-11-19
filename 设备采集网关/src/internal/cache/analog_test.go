package cache

import (
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"testing"
	"time"
)

func TestAnalogToggleRequiresNewSampleAndRetainsCounters(t *testing.T) {
	c := New()
	m := config.Default(1).Meters[0]
	m.Analog = true
	c.Bind(m)
	c.Update(1, 5, []uint16{12345, 4000, 65036, 1234}, true)
	c.Fail(1, 5, modbus.IllegalAddress, true)
	c.mu.Lock()
	c.meters[0].Groups[5].At = time.Now().Add(-5 * time.Second)
	c.mu.Unlock()
	v := c.Snapshot(time.Now(), 3000, true)[0]
	if v.Groups[5].AgeMS < 5000 || v.Raw[27] != 12345 {
		t.Fatal("missing sample age", v.Groups[5])
	}
	m.Analog = false
	c.SetConfig(m)
	v = c.Snapshot(time.Now(), 3000, true)[0]
	if v.Groups[5].TimeMS != 0 || v.Groups[5].Unsupported || v.Groups[5].Sequence != 1 || v.Groups[5].Failures != 1 || v.Known[27] || v.Raw[27] != 0 {
		t.Fatal("toggle kept stale sample or lost counters", v.Groups[5])
	}
	if v.Groups[5].AgeMS != ^uint32(0) {
		t.Fatal("unsampled age not sentinel")
	}
	m.Analog = true
	c.SetConfig(m)
	c.Update(1, 5, []uint16{20000, 12000, 0, 65535}, false)
	v = c.Snapshot(time.Now(), 3000, false)[0]
	if v.Groups[5].Sequence != 2 || v.Groups[5].Failures != 1 || v.Raw[27] != 20000 || v.Groups[5].TimeTrusted {
		t.Fatal(v.Groups[5])
	}
}

package cache

import (
	"levelgateway/protocols"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"sync"
	"time"
)

type Group struct {
	TimeTrusted bool      `json:"time_trusted"`
	TimeMS      int64     `json:"time_ms"`
	Sequence    uint32    `json:"sequence"`
	Failures    uint32    `json:"failures"`
	Unsupported bool      `json:"unsupported"`
	Error       string    `json:"error,omitempty"`
	AgeMS       uint32    `json:"age_ms"`
	At          time.Time `json:"-"`
}
type Meter struct {
	Config          config.Meter `json:"config"`
	Raw             [53]uint16   `json:"raw"`
	Known           [53]bool     `json:"known"`
	Groups          [6]Group     `json:"groups"`
	Sequence        uint32       `json:"sequence"`
	FastCompleteMS  int64        `json:"fast_complete_ms"`
	LastAny         time.Time    `json:"-"`
	Parameters      [42]uint16   `json:"parameters"`
	ParameterTimeMS int64        `json:"parameter_time_ms"`
	ParameterError  string       `json:"parameter_error,omitempty"`
}
type View struct {
	Meter
	Quality          uint16  `json:"quality"`
	Valid            uint16  `json:"valid"`
	AgeMS            uint32  `json:"age_ms"`
	LevelMM          float64 `json:"level_mm"`
	TemperatureC     float64 `json:"temperature_c"`
	DensityKgM3      float64 `json:"density_kg_m3"`
	DetailPeriodMS   int     `json:"detail_period_ms"`
	CollectionPaused bool    `json:"collection_paused"`
}
type Cache struct {
	mu     sync.RWMutex
	meters [config.MaxMeters]Meter
	spec   protocols.Spec
}

func New() *Cache { return &Cache{spec: protocols.Load()} }
func (c *Cache) Bind(m config.Meter) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.meters[m.Slot-1] = Meter{Config: m}
}
func (c *Cache) SetConfig(m config.Meter) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.meters[m.Slot-1].Config.Binding != m.Binding {
		c.meters[m.Slot-1] = Meter{Config: m}
	} else {
		if c.meters[m.Slot-1].Config.Analog != m.Analog {
			meter := &c.meters[m.Slot-1]
			old := meter.Groups[5]
			// Keep counters, but require a new sample after re-enabling. A previous
			// IllegalAddress must not suppress the first retry after a toggle.
			meter.Groups[5] = Group{Sequence: old.Sequence, Failures: old.Failures}
			for address := 27; address <= 30; address++ {
				meter.Raw[address] = 0
				meter.Known[address] = false
			}
		}
		c.meters[m.Slot-1].Config = m
	}
}
func (c *Cache) Clear(slot int) { c.mu.Lock(); defer c.mu.Unlock(); c.meters[slot-1] = Meter{} }
func (c *Cache) Update(slot, group int, words []uint16, trusted ...bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	m := &c.meters[slot-1]
	g := c.spec.Groups[group]
	for i, v := range words {
		a := int(g.Start) + i
		m.Raw[a] = v
		m.Known[a] = true
	}
	now := time.Now()
	old := m.Groups[group]
	m.Groups[group] = Group{TimeMS: now.UnixMilli(), Sequence: old.Sequence + 1, Failures: old.Failures, At: now}
	m.Groups[group].TimeTrusted = len(trusted) > 0 && trusted[0]
	m.LastAny = now
	m.Sequence++
}
func (c *Cache) InvalidateTimeQuality() {
	c.mu.Lock()
	defer c.mu.Unlock()
	for i := range c.meters {
		for j := range c.meters[i].Groups {
			c.meters[i].Groups[j].TimeTrusted = false
		}
	}
}
func (c *Cache) Fail(slot, group int, err error, unsupported bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	g := &c.meters[slot-1].Groups[group]
	g.Failures++
	g.Error = err.Error()
	g.Unsupported = unsupported
}
func (c *Cache) Complete(slot int) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.meters[slot-1].FastCompleteMS = time.Now().UnixMilli()
}
func (c *Cache) Parameters(slot int, words []uint16, err error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	m := &c.meters[slot-1]
	if err != nil {
		m.ParameterError = err.Error()
		return
	}
	copy(m.Parameters[:], words)
	m.ParameterTimeMS = time.Now().UnixMilli()
	m.ParameterError = ""
}
func age(at, timeNow time.Time) uint32 {
	if at.IsZero() {
		return ^uint32(0)
	}
	v := timeNow.Sub(at).Milliseconds()
	if v < 0 {
		return 0
	}
	if v > int64(^uint32(0)) {
		return ^uint32(0)
	}
	return uint32(v)
}
func (c *Cache) Snapshot(now time.Time, fastMS int, timeTrusted bool) []View {
	c.mu.RLock()
	defer c.mu.RUnlock()
	out := make([]View, config.MaxMeters)
	for i, m := range c.meters {
		v := View{Meter: m}
		for group := range v.Groups {
			v.Groups[group].AgeMS = age(m.Groups[group].At, now)
		}
		if m.Config.Slot == 0 || !m.Config.Enabled {
			v.Quality = 1 << 4
			out[i] = v
			continue
		}
		v.Quality = 0
		if !timeTrusted {
			v.Quality |= 1 << 7
		}
		if !m.LastAny.IsZero() {
			v.Quality |= 1
		}
		if age(m.LastAny, now) <= uint32(3*fastMS) {
			v.Quality |= 2
		}
		all := true
		v.AgeMS = 0
		good := [3]bool{}
		for j := 0; j < 3; j++ {
			g := m.Groups[j]
			if g.Error != "" {
				v.Quality |= 1 << 8
			}
			if !g.TimeTrusted {
				v.Quality |= 1 << 7
			}
			a := age(g.At, now)
			if a > v.AgeMS {
				v.AgeMS = a
			}
			good[j] = !g.At.IsZero() && !g.Unsupported && a <= uint32(3*fastMS)
			if !good[j] {
				all = false
			}
			if g.Unsupported {
				v.Quality |= 1 << 5
			}
		}
		if !all {
			v.Quality |= 1<<2 | 1<<8
		}
		if m.Known[52] && m.Raw[52] != 0 {
			v.Quality |= 1 << 3
		}
		if good[0] {
			v.Valid |= 1<<0 | 1<<6
		}
		if good[1] {
			v.Valid |= 1 << 1
		}
		if good[2] {
			v.Valid |= 1<<2 | 1<<3 | 1<<4 | 1<<5
		}
		v.LevelMM = float64(int32(modbus.Get32(m.Raw[:2]))) / 10
		v.TemperatureC = float64(int16(m.Raw[26])) / 100
		v.DensityKgM3 = float64(m.Raw[50]) / 10
		out[i] = v
	}
	return out
}
func (c *Cache) Summary(v View, trusted bool) []uint16 {
	w := make([]uint16, protocols.SummaryStride)
	w[protocols.SummaryQuality] = v.Quality
	w[protocols.SummaryValid] = v.Valid
	modbus.Put32(w[protocols.SummarySequence:], v.Sequence)
	copy(w[protocols.SummaryLevel:protocols.SummaryLevel+2], v.Raw[:2])
	w[protocols.SummaryAverageTemperature] = v.Raw[26]
	w[protocols.SummaryCurrentDensity] = v.Raw[50]
	w[protocols.SummaryAverageDensity] = v.Raw[49]
	w[protocols.SummaryFloatState] = v.Raw[51]
	w[protocols.SummaryFault] = v.Raw[52]
	w[protocols.SummaryLevelRate] = v.Raw[3]
	modbus.Put64(w[protocols.SummaryLastCompleteFastMs:], uint64(v.FastCompleteMS))
	modbus.Put32(w[protocols.SummaryOldestFastAgeMs:], v.AgeMS)
	w[protocols.SummaryPhysicalPort] = uint16(v.Config.Port)
	w[protocols.SummaryMeterAddress] = uint16(v.Config.Address)
	modbus.Put32(w[protocols.SummaryBindingVersion:], v.Config.Binding)
	if trusted && v.Quality&(1<<7) == 0 {
		w[protocols.SummaryTimeQuality] = 1
	}
	return w
}
func (c *Cache) Detail(v View, now time.Time) []uint16 {
	w := make([]uint16, 144)
	copy(w, v.Raw[:])
	modbus.Put32(w[54:], v.Config.Binding)
	modbus.Put32(w[56:], v.Sequence)
	for i, known := range v.Known {
		if known {
			w[58+i/16] |= 1 << uint(15-i%16)
		}
	}
	for i, g := range v.Groups {
		b := 64 + i*12
		status := uint16(0)
		if !g.At.IsZero() {
			status = 1
		}
		if g.Error != "" {
			status |= 2
		}
		if g.Unsupported {
			status |= 4
		}
		if g.TimeTrusted {
			status |= 8
		}
		w[b] = status
		n := g.Failures
		if n > 65535 {
			n = 65535
		}
		w[b+1] = uint16(n)
		modbus.Put32(w[b+2:], g.Sequence)
		modbus.Put64(w[b+4:], uint64(g.TimeMS))
		modbus.Put32(w[b+8:], age(g.At, now))
		w[b+10] = c.spec.Groups[i].Words
		w[b+11] = c.spec.Groups[i].Start
	}
	return w
}

// Invalidate instead of displaying a pre-calibration level as a current value.
func (c *Cache) InvalidateLevel(slot int, err error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	m := &c.meters[slot-1]
	for i := 0; i < 6; i++ {
		m.Known[i] = false
	}
	m.Groups[0].Error = err.Error()
	m.Groups[0].At = time.Time{}
	m.Groups[0].Failures++
}

package core

import (
	"encoding/json"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/fw9000"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestAnalogSwitchPersistsPollsAndMapsToNorth(t *testing.T) {
	m := testManager(t, 1)
	until(t, func() bool { return m.Views()[0].Groups[0].Sequence > 0 })
	if m.Views()[0].Groups[5].Sequence != 0 {
		t.Fatal("disabled analog collected")
	}
	c := m.Config()
	c.DetailMS = 1000
	c.Meters[0].Analog = true
	if err := m.Apply(c); err != nil {
		t.Fatal(err)
	}
	loaded, err := config.Load(m.path)
	if err != nil || !loaded.Meters[0].Analog {
		t.Fatal("analog switch not persisted", err)
	}
	for i, v := range []uint16{12345, 4000, 65036, 1234} {
		m.Ports[1].Simulator.SetRaw(1, 27+i, v)
	}
	until(t, func() bool { return m.Views()[0].Groups[5].Sequence > 0 && m.Views()[0].Raw[27] == 12345 })
	words, err := m.Registers(4, 3027, 4)
	if err != nil || len(words) != 4 || words[0] != 12345 || words[2] != 65036 {
		t.Fatal(words, err)
	}
	if m.Views()[0].DetailPeriodMS != 1000 || m.Views()[0].CollectionPaused {
		t.Fatal("missing committed collection context")
	}
	c = m.Config()
	c.Meters[0].Analog = false
	if err = m.Apply(c); err != nil {
		t.Fatal(err)
	}
	seq := m.Views()[0].Groups[5].Sequence
	until(t, func() bool { return m.Views()[0].Groups[0].AgeMS < 100 })
	time.Sleep(1100 * time.Millisecond)
	if m.Views()[0].Groups[5].Sequence != seq || m.Views()[0].Groups[5].TimeMS != 0 {
		t.Fatal("disabled analog still polling")
	}
	c = m.Config()
	c.Meters[0].Analog = true
	if err = m.Apply(c); err != nil {
		t.Fatal(err)
	}
	until(t, func() bool { return m.Views()[0].Groups[5].Sequence > seq })
	m.Ports[1].Simulator.SetFault(1, fw9000.Fault{UnsupportedStart: 27})
	until(t, func() bool { return m.Views()[0].Groups[5].Unsupported })
	// Off/on must clear unsupported suppression and try the new port instance.
	c = m.Config()
	c.Meters[0].Analog = false
	if err = m.Apply(c); err != nil {
		t.Fatal(err)
	}
	c = m.Config()
	c.Meters[0].Analog = true
	if err = m.Apply(c); err != nil {
		t.Fatal(err)
	}
	until(t, func() bool { return m.Views()[0].Groups[5].TimeMS != 0 && !m.Views()[0].Groups[5].Unsupported })
	h := m.API("token")
	request := httptest.NewRequest("GET", "/api/data", nil)
	request.Header.Set("Authorization", "Bearer token")
	w := httptest.NewRecorder()
	h.ServeHTTP(w, request)
	var data []map[string]any
	if json.Unmarshal(w.Body.Bytes(), &data) != nil || !strings.Contains(w.Body.String(), "age_ms") || data[0]["detail_period_ms"] != float64(1000) {
		t.Fatal("API collection context", w.Body.String())
	}
}

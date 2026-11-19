package core

import (
	"encoding/json"
	"errors"
	"levelgateway/src/internal/clockstate"
	"net/http/httptest"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestClockAPIPartialSaveAndStatus(t *testing.T) {
	m := testManager(t, 1)
	now := time.Now()
	m.clockMu.Lock()
	m.clock = clockstate.New(filepath.Join(t.TempDir(), "clock-state.json"), "/dev/rtc0", false, clockstate.Ops{
		Now: func() time.Time { return now }, Sleep: func(time.Duration) {},
		SetSystem: func(time.Time) error { t.Fatal("API unexpectedly changed host time"); return nil },
		ReadRTC:   func(string) (clockstate.RTCReading, error) { return clockstate.RTCReading{}, errors.New("no RTC") },
		WriteRTC:  func(string, time.Time) error { return errors.New("no RTC") },
	})
	m.clockMu.Unlock()
	h := m.API("secret", true)
	req := httptest.NewRequest("POST", "/api/time", strings.NewReader(`{"utc_ms":`+jsonNumber(now.UnixMilli())+`}`))
	req.Header.Set("Authorization", "Bearer secret")
	w := httptest.NewRecorder()
	h.ServeHTTP(w, req)
	var result struct {
		Ok    bool              `json:"ok"`
		Clock clockstate.Status `json:"clock"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &result); err != nil || w.Code != 200 {
		t.Fatal(w.Code, w.Body.String(), err)
	}
	if !result.Ok || !result.Clock.Trusted || result.Clock.RTCSaved || result.Clock.Warning == "" {
		t.Fatal(result)
	}
	req = httptest.NewRequest("GET", "/api/status", nil)
	req.Header.Set("Authorization", "Bearer secret")
	w = httptest.NewRecorder()
	h.ServeHTTP(w, req)
	var status struct {
		Trusted bool              `json:"time_trusted"`
		Clock   clockstate.Status `json:"clock"`
	}
	if err := json.Unmarshal(w.Body.Bytes(), &status); err != nil {
		t.Fatal(err)
	}
	if !status.Trusted || status.Clock.Source != "browser" || status.Clock.Warning == "" {
		t.Fatal(status)
	}
}

func jsonNumber(n int64) string { b, _ := json.Marshal(n); return string(b) }

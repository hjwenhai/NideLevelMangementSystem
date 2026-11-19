package clockstate

import (
	"encoding/json"
	"errors"
	"levelgateway/src/internal/config"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

type fakeClock struct {
	now, rtc                      time.Time
	readErr, writeErr, setErr     error
	flags                         uint32
	validity, stopped, wrongWrite bool
	sets, writes                  int
}

func (f *fakeClock) ops() Ops {
	return Ops{
		Now: func() time.Time { return f.now },
		Sleep: func(d time.Duration) {
			f.now = f.now.Add(d)
			if !f.stopped {
				f.rtc = f.rtc.Add(d)
			}
		},
		SetSystem: func(t time.Time) error {
			f.sets++
			if f.setErr == nil {
				f.now = t
			}
			return f.setErr
		},
		ReadRTC: func(string) (RTCReading, error) {
			return RTCReading{Time: f.rtc.Truncate(time.Second), Flags: f.flags, ValiditySupported: f.validity}, f.readErr
		},
		WriteRTC: func(_ string, t time.Time) error {
			f.writes++
			if f.writeErr == nil && !f.wrongWrite {
				f.rtc = t
			}
			return f.writeErr
		},
	}
}
func fixture(t *testing.T) (*Controller, *fakeClock) {
	t.Helper()
	f := &fakeClock{now: time.Date(2026, 10, 16, 1, 0, 0, 0, time.UTC), validity: true}
	f.rtc = f.now
	c := New(filepath.Join(t.TempDir(), "clock-state.json"), "/dev/rtc0", false, f.ops())
	return c, f
}
func calibrated(t *testing.T) (*Controller, *fakeClock) {
	t.Helper()
	c, f := fixture(t)
	if err := c.Calibrate(f.now.UnixMilli(), true); err != nil {
		t.Fatal(err)
	}
	if !c.Status.Trusted || !c.Status.RTCSaved {
		t.Fatal(c.Status)
	}
	return c, f
}
func cold(c *Controller, f *fakeClock) *Controller { return New(c.Path, c.Device, false, f.ops()) }

func TestColdBootAndProcessRestart(t *testing.T) {
	for _, gap := range []time.Duration{0, 10 * time.Second, 30 * time.Minute, 24 * time.Hour} {
		t.Run(gap.String(), func(t *testing.T) {
			c, f := calibrated(t)
			f.now = f.now.Add(gap)
			f.rtc = f.rtc.Add(gap)
			n := cold(c, f)
			n.Restore(true)
			if !n.Status.Trusted || n.Status.Source != "rtc" {
				t.Fatal(n.Status)
			}
			if f.sets != 0 || f.writes != 1 {
				t.Fatal("boot should only read an already matching RTC")
			}
		})
	}
}

func TestRestoreBadSystemClockBeforeMeasurements(t *testing.T) {
	c, f := calibrated(t)
	f.rtc = f.rtc.Add(time.Hour)
	f.now = time.Unix(0, 0)
	n := cold(c, f)
	n.Restore(true)
	if !n.Status.Trusted || f.sets != 1 || !near(f.now, f.rtc) {
		t.Fatal(n.Status, f)
	}
}

func TestRestoreRejectsUnprovenClock(t *testing.T) {
	for _, name := range []string{"missing", "corrupt", "schema", "invalidated", "not-saved", "different-device", "rtc-absent", "battery", "invalid-date", "regressed", "too-old", "stopped", "set-denied", "set-failed"} {
		t.Run(name, func(t *testing.T) {
			c, f := calibrated(t)
			switch name {
			case "missing":
				os.Remove(c.Path)
			case "corrupt":
				os.WriteFile(c.Path, []byte("{"), 0600)
			case "schema":
				c.Record.Schema = 2
				c.save()
			case "invalidated":
				c.Invalidate("外部校时跳变")
			case "not-saved":
				c.Record.RTCSaved = false
				c.save()
			case "different-device":
				c.Record.RTCDevice = "/dev/rtc1"
				c.save()
			case "rtc-absent":
				f.readErr = errors.New("no RTC")
			case "battery":
				f.flags = 2
			case "invalid-date":
				f.rtc = time.Unix(0, 0)
			case "regressed":
				f.rtc = f.rtc.Add(-time.Hour)
			case "too-old":
				f.rtc = f.rtc.Add(367 * 24 * time.Hour)
			case "stopped":
				f.stopped = true
			case "set-denied", "set-failed":
				f.now = time.Unix(0, 0)
				f.setErr = errors.New("permission denied")
			}
			n := cold(c, f)
			n.Restore(name != "set-denied")
			if n.Status.Trusted {
				t.Fatal("unproven RTC trusted", n.Status)
			}
			if f.writes != 1 {
				t.Fatal("startup wrote RTC")
			}
			if name == "set-denied" && f.sets != 0 {
				t.Fatal("changed system without permission")
			}
		})
	}
}

func TestCalibrationPartialRTCSuccess(t *testing.T) {
	for _, name := range []string{"no-rtc", "write-error", "bad-readback", "flags", "no-permission", "flags-unsupported"} {
		t.Run(name, func(t *testing.T) {
			c, f := fixture(t)
			switch name {
			case "no-rtc":
				f.readErr = errors.New("missing")
			case "write-error":
				f.writeErr = errors.New("read only")
			case "bad-readback":
				f.rtc = f.rtc.Add(-time.Hour)
				f.wrongWrite = true
			case "flags":
				f.flags = 1
			case "flags-unsupported":
				f.validity = false
			}
			if err := c.Calibrate(f.now.UnixMilli(), name != "no-permission"); err != nil {
				t.Fatal(err)
			}
			if !c.Status.Trusted || c.Status.Warning == "" {
				t.Fatal(c.Status)
			}
			n := cold(c, f)
			n.Restore(true)
			if n.Status.Trusted != (name == "flags-unsupported") {
				t.Fatal(n.Status)
			}
		})
	}
}

func TestCheckpointPreventsReturningToOldRTC(t *testing.T) {
	c, f := calibrated(t)
	old := f.rtc
	f.now = f.now.Add(5 * time.Minute)
	f.rtc = f.now
	if err := c.Checkpoint(); err != nil {
		t.Fatal(err)
	}
	f.rtc = old
	n := cold(c, f)
	n.Restore(true)
	if n.Status.Trusted {
		t.Fatal("accepted RTC older than checkpoint")
	}
}

func TestPersistenceFailureDoesNotWriteClock(t *testing.T) {
	c, f := fixture(t)
	c.Path = t.TempDir()
	if err := c.Calibrate(f.now.Add(time.Hour).UnixMilli(), true); err == nil {
		t.Fatal("record failure ignored")
	}
	if f.sets != 0 || f.writes != 0 || c.Status.Trusted {
		t.Fatal("clock written before tombstone")
	}
}

func TestFailedRecalibrationCannotRestorePriorRecord(t *testing.T) {
	c, f := calibrated(t)
	f.setErr = errors.New("denied")
	if err := c.Calibrate(f.now.Add(time.Hour).UnixMilli(), true); err == nil {
		t.Fatal("clock failure ignored")
	}
	n := cold(c, f)
	n.Restore(true)
	if n.Status.Trusted {
		t.Fatal("stale calibration survived partial update")
	}
}

func TestFinalRecordFailureKeepsTombstone(t *testing.T) {
	c, f := calibrated(t)
	writes := 0
	c.Ops.WriteRecord = func(path string, b []byte) error {
		writes++
		if writes == 2 {
			return errors.New("disk full")
		}
		return config.WriteAtomic(path, b)
	}
	if err := c.Calibrate(f.now.UnixMilli(), true); err == nil || c.Status.Trusted {
		t.Fatal(err, c.Status)
	}
	n := cold(c, f)
	n.Restore(true)
	if n.Status.Trusted {
		t.Fatal("trusted incomplete final record")
	}
}

func TestInvalidDateAndPermissionDoNotTouchClocks(t *testing.T) {
	for _, name := range []string{"date", "permission"} {
		t.Run(name, func(t *testing.T) {
			c, f := fixture(t)
			target := f.now.Add(time.Hour).UnixMilli()
			if name == "date" {
				target = 0
			}
			if err := c.Calibrate(target, false); err == nil {
				t.Fatal("accepted invalid request")
			}
			if f.sets != 0 || f.writes != 0 {
				t.Fatal("changed clocks without authorization")
			}
			if _, err := os.Stat(c.Path); !os.IsNotExist(err) {
				t.Fatal("changed prior calibration on rejected request")
			}
		})
	}
}

func TestSlowDiskDoesNotSetClockToOldRequest(t *testing.T) {
	for _, allow := range []bool{false, true} {
		t.Run(jsonNumberBool(allow), func(t *testing.T) {
			c, f := fixture(t)
			start := f.now
			c.Ops.WriteRecord = func(path string, b []byte) error {
				f.now = f.now.Add(5 * time.Second)
				f.rtc = f.rtc.Add(5 * time.Second)
				return config.WriteAtomic(path, b)
			}
			if err := c.Calibrate(start.UnixMilli(), allow); err != nil {
				t.Fatal(err)
			}
			if f.sets != 0 || !f.now.Equal(start.Add(10*time.Second)) {
				t.Fatal("clock regressed to stale browser request", f.now, f.sets)
			}
		})
	}
}

func jsonNumberBool(v bool) string {
	if v {
		return "write-enabled"
	}
	return "read-only"
}

func TestUTCRecordAndSimulationIsolation(t *testing.T) {
	c, f := fixture(t)
	target := f.now.In(time.FixedZone("CST", 8*3600))
	if err := c.Calibrate(target.UnixMilli(), true); err != nil {
		t.Fatal(err)
	}
	b, err := os.ReadFile(c.Path)
	if err != nil {
		t.Fatal(err)
	}
	var r Record
	if err = json.Unmarshal(b, &r); err != nil || r.CalibratedMS != target.UnixMilli() {
		t.Fatal(string(b), err)
	}
	sim := New(filepath.Join(t.TempDir(), "sim.json"), "/dev/rtc0", true, f.ops())
	writes, sets := f.writes, f.sets
	sim.Restore(true)
	if sim.Status.Trusted {
		t.Fatal("simulation restored real clock trust")
	}
	if err = sim.Calibrate(f.now.UnixMilli(), true); err != nil {
		t.Fatal(err)
	}
	if _, err = os.Stat(sim.Path); !os.IsNotExist(err) {
		t.Fatal("simulation wrote provenance")
	}
	if err = sim.Calibrate(f.now.Add(-time.Hour).UnixMilli(), true); err == nil || !strings.Contains(err.Error(), "模拟模式") {
		t.Fatal(err)
	}
	if f.writes != writes || f.sets != sets {
		t.Fatal("simulation accessed host clocks")
	}
}

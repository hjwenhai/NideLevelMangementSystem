package storage

import (
	"path/filepath"
	"testing"
)

func TestResetAuditFailureRollsBackStateAndLegacyResolution(t *testing.T) {
	s, err := Open(filepath.Join(t.TempDir(), "db"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	if err = s.SaveCommand("old", 6, map[string]any{"request": map[string]any{"slot": 1}, "state": 6}); err != nil {
		t.Fatal(err)
	}
	if err = s.SaveControls([]ControlChange{{Slot: 1, Data: map[string]any{"state": 5}}}, "fixture", "before"); err != nil {
		t.Fatal(err)
	}
	if _, err = s.db.Exec(`CREATE TRIGGER reject_reset_audit BEFORE INSERT ON events WHEN NEW.kind='control_reset' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END`); err != nil {
		t.Fatal(err)
	}
	if err = s.SaveControls([]ControlChange{{Slot: 1, Data: map[string]any{"state": 0}, ResolveLegacy: true}}, "control_reset", "manual"); err == nil {
		t.Fatal("audit failure accepted reset")
	}
	states, err := s.LoadControls()
	if err != nil || string(states[1]) != `{"state":5}` {
		t.Fatal("state committed without audit", states, err)
	}
	legacy, err := s.LegacyUnresolved()
	if err != nil || len(legacy) != 1 {
		t.Fatal("legacy resolution committed without audit", err)
	}
	if _, err = s.db.Exec(`DROP TRIGGER reject_reset_audit`); err != nil {
		t.Fatal(err)
	}
	if err = s.SaveControls([]ControlChange{{Slot: 1, Data: map[string]any{"state": 0}, ResolveLegacy: true}}, "control_reset", "manual"); err != nil {
		t.Fatal(err)
	}
	legacy, err = s.LegacyUnresolved()
	if err != nil || len(legacy) != 0 {
		t.Fatal("legacy remained unresolved after committed reset", err)
	}
}

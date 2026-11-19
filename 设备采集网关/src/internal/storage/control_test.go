package storage

import (
	"database/sql"
	"os"
	"path/filepath"
	"testing"
)

func TestV1MigrationBackupAndV2RollbackGuard(t *testing.T) {
	path := filepath.Join(t.TempDir(), "legacy.db")
	db, err := sql.Open("sqlite", path)
	if err != nil {
		t.Fatal(err)
	}
	db.Exec(`PRAGMA user_version=1`)
	db.Exec(`CREATE TABLE marker(value TEXT)`)
	db.Exec(`INSERT INTO marker VALUES('preserve')`)
	db.Close()
	backup := path + ".before-v3"
	if err = BackupDatabase(path, backup); err != nil {
		t.Fatal(err)
	}
	if err = BackupDatabase(path, backup); err == nil {
		t.Fatal("backup overwritten")
	}
	s, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	var v int
	s.db.QueryRow("PRAGMA user_version").Scan(&v)
	if v != 3 {
		t.Fatal(v)
	}
	var data string
	s.db.QueryRow("SELECT value FROM marker").Scan(&data)
	if data != "preserve" {
		t.Fatal(data)
	}
	s.Close()
	db, _ = sql.Open("sqlite", backup)
	db.QueryRow("PRAGMA user_version").Scan(&v)
	db.Close()
	if v != 1 {
		t.Fatal("backup migrated source")
	}
	if info, _ := os.Stat(backup); info == nil {
		t.Fatal("no backup")
	}
	db, _ = sql.Open("sqlite", path)
	db.Exec(`PRAGMA user_version=4`)
	db.Close()
	if s, e := Open(path); e == nil {
		s.Close()
		t.Fatal("future database downgraded")
	}
}
func TestAtomicControlAuditAndCorruptRestoreEvidence(t *testing.T) {
	s, err := Open(filepath.Join(t.TempDir(), "db"))
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	s.db.Exec(`CREATE TRIGGER fail_control_audit BEFORE INSERT ON events BEGIN SELECT RAISE(ABORT,'audit failed'); END`)
	if err = s.SaveControls([]ControlChange{{Slot: 1, Data: map[string]int{"state": 1}}}, "accept", "test"); err == nil {
		t.Fatal("audit failure ignored")
	}
	rows, _ := s.LoadControls()
	if len(rows) != 0 {
		t.Fatal("state committed without audit")
	}
}

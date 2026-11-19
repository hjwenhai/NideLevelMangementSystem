package storage

import (
	"database/sql"
	"path/filepath"
	"testing"
)

func TestSchema2ControlMigrationPreservesUnknownAndAllows64(t *testing.T) {
	path := filepath.Join(t.TempDir(), "old.db")
	db, _ := sql.Open("sqlite", path)
	for _, q := range []string{`CREATE TABLE control_slots(slot INTEGER PRIMARY KEY CHECK(slot BETWEEN 1 AND 32),data TEXT NOT NULL)`, `INSERT INTO control_slots VALUES(32,'{"state":5,"request":{"id":"keep"}}')`, `PRAGMA user_version=2`} {
		if _, err := db.Exec(q); err != nil {
			t.Fatal(err)
		}
	}
	db.Close()
	if err := BackupDatabase(path, path+".before"); err != nil {
		t.Fatal(err)
	}
	s, err := Open(path)
	if err != nil {
		t.Fatal(err)
	}
	rows, err := s.LoadControls()
	if err != nil || string(rows[32]) != `{"state":5,"request":{"id":"keep"}}` {
		t.Fatal("lost unknown", rows, err)
	}
	if err = s.SaveControls([]ControlChange{{Slot: 64, Data: map[string]int{"state": 5}}}, "test64", "retain"); err != nil {
		t.Fatal(err)
	}
	s.Close()
	s, err = Open(path)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	rows, err = s.LoadControls()
	if err != nil || len(rows) != 2 {
		t.Fatal(rows, err)
	}
	db, _ = sql.Open("sqlite", path+".before")
	defer db.Close()
	var v int
	db.QueryRow(`PRAGMA user_version`).Scan(&v)
	if v != 2 {
		t.Fatal("backup changed")
	}
}

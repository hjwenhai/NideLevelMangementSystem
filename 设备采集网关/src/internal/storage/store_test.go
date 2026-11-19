package storage

import (
	"levelgateway/src/internal/cache"
	"levelgateway/src/internal/config"
	"path/filepath"
	"testing"
	"time"
)

func TestHistoryCursorCleanupAndReopen(t *testing.T) {
	path := filepath.Join(t.TempDir(), "db")
	s, e := Open(path)
	if e != nil {
		t.Fatal(e)
	}
	c := cache.New()
	for _, m := range config.Default(2).Meters {
		c.Bind(m)
	}
	now := time.Now().UnixMilli()
	for _, ts := range []int64{now - 31*86400000, now - 1000, now} {
		if e = s.History(c.Snapshot(time.Now(), 1000, false), ts); e != nil {
			t.Fatal(e)
		}
	}
	rows, e := s.Query(0, now, 0, 0, 0, 1)
	if e != nil || len(rows) != 1 {
		t.Fatal(e, rows)
	}
	next, e := s.Query(0, now, rows[0].RecordMS, rows[0].Slot, 0, 1)
	if e != nil || len(next) != 1 || next[0].Slot != 2 {
		t.Fatal("cursor skipped same timestamp", e, next)
	}
	if e = s.Cleanup(30, 16); e != nil {
		t.Fatal(e)
	}
	s.Close()
	s, e = Open(path)
	if e != nil {
		t.Fatal(e)
	}
	defer s.Close()
	rows, e = s.Query(0, now, 0, 0, 0, 100)
	if e != nil || len(rows) != 4 {
		t.Fatal("retention/reopen", e, rows)
	}
}

package storage

import (
	"context"
	"database/sql"
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/cache"
	_ "modernc.org/sqlite"
	"os"
	"path/filepath"
	"sync"
	"time"
)

type Store struct {
	db              *sql.DB
	path            string
	mu              sync.Mutex
	errMu           sync.RWMutex
	lastError       string
	capacityWarning string
}
type Record struct {
	RecordMS       int64  `json:"record_ms"`
	Slot           int    `json:"slot"`
	Binding        uint32 `json:"binding_version"`
	ReadMS         int64  `json:"read_ms"`
	Level          int32  `json:"level_raw"`
	Temperature    int16  `json:"temperature_raw"`
	Density        uint16 `json:"density_raw"`
	AverageDensity uint16 `json:"average_density_raw"`
	State          uint16 `json:"state"`
	Fault          uint16 `json:"fault"`
	Quality        uint16 `json:"quality"`
	Valid          uint16 `json:"valid"`
	LevelMS        int64  `json:"level_ms"`
	TemperatureMS  int64  `json:"temperature_ms"`
	DensityMS      int64  `json:"density_ms"`
}

func Open(path string) (*Store, error) {
	if e := os.MkdirAll(filepath.Dir(path), 0700); e != nil {
		return nil, e
	}
	db, e := sql.Open("sqlite", path)
	if e != nil {
		return nil, e
	}
	db.SetMaxOpenConns(1)
	s := &Store{db: db, path: path}
	for _, q := range []string{"PRAGMA journal_mode=WAL", "PRAGMA synchronous=FULL", "PRAGMA busy_timeout=2000", `CREATE TABLE IF NOT EXISTS commands(key TEXT PRIMARY KEY, state INTEGER NOT NULL, data TEXT NOT NULL, updated_ms INTEGER NOT NULL)`, `CREATE TABLE IF NOT EXISTS history(record_ms INTEGER NOT NULL,slot INTEGER NOT NULL,binding INTEGER NOT NULL,read_ms INTEGER NOT NULL,level INTEGER NOT NULL,temp INTEGER NOT NULL,density INTEGER NOT NULL,avg_density INTEGER NOT NULL,state INTEGER NOT NULL,fault INTEGER NOT NULL,quality INTEGER NOT NULL,valid INTEGER NOT NULL,PRIMARY KEY(record_ms,slot)) WITHOUT ROWID`, `CREATE INDEX IF NOT EXISTS history_slot ON history(slot,binding,record_ms)`, `CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY,time_ms INTEGER NOT NULL,kind TEXT NOT NULL,message TEXT NOT NULL)`} {
		if _, e = db.Exec(q); e != nil {
			db.Close()
			return nil, e
		}
	}
	var version int
	if e = db.QueryRow("PRAGMA user_version").Scan(&version); e != nil || version > 3 {
		db.Close()
		return nil, fmt.Errorf("数据库版本不兼容: %d (%v)", version, e)
	}
	rows, e := db.Query("PRAGMA table_info(history)")
	if e != nil {
		db.Close()
		return nil, e
	}
	columns := map[string]bool{}
	for rows.Next() {
		var cid, notnull, pk int
		var name, kind string
		var def any
		if e = rows.Scan(&cid, &name, &kind, &notnull, &def, &pk); e != nil {
			break
		}
		columns[name] = true
	}
	rowErr := rows.Err()
	rows.Close()
	if e != nil || rowErr != nil {
		db.Close()
		return nil, fmt.Errorf("读取数据库结构: %v %v", e, rowErr)
	}
	for _, name := range []string{"level_ms", "temp_ms", "density_ms"} {
		if !columns[name] {
			if _, e = db.Exec("ALTER TABLE history ADD COLUMN " + name + " INTEGER NOT NULL DEFAULT 0"); e != nil {
				db.Close()
				return nil, e
			}
		}
	}
	if _, e = db.Exec(`CREATE TABLE IF NOT EXISTS control_slots(slot INTEGER PRIMARY KEY CHECK(slot BETWEEN 1 AND 32),data TEXT NOT NULL)`); e != nil {
		db.Close()
		return nil, e
	}
	// Rebuild the CHECK constraint transactionally; history, events and cycle JSON stay intact.
	if version < 3 {
		tx, err := db.Begin()
		if err != nil {
			db.Close()
			return nil, err
		}
		for _, q := range []string{
			`CREATE TABLE control_slots_64(slot INTEGER PRIMARY KEY CHECK(slot BETWEEN 1 AND 64),data TEXT NOT NULL)`,
			`INSERT INTO control_slots_64 SELECT slot,data FROM control_slots`,
			`DROP TABLE control_slots`, `ALTER TABLE control_slots_64 RENAME TO control_slots`,
			`PRAGMA user_version=3`,
		} {
			if _, err = tx.Exec(q); err != nil {
				tx.Rollback()
				db.Close()
				return nil, err
			}
		}
		if err = tx.Commit(); err != nil {
			db.Close()
			return nil, err
		}
	}
	if _, e = db.Exec("PRAGMA user_version=3"); e != nil {
		db.Close()
		return nil, e
	}
	return s, nil
}
func (s *Store) Close() error { return s.db.Close() }
func (s *Store) note(e error) {
	s.errMu.Lock()
	defer s.errMu.Unlock()
	if e != nil {
		s.lastError = e.Error()
	} else {
		s.lastError = ""
	}
}
func (s *Store) Status() string {
	s.errMu.RLock()
	defer s.errMu.RUnlock()
	if s.lastError != "" {
		return s.lastError
	}
	return s.capacityWarning
}
func (s *Store) SaveCommand(key string, state int, v any) error {
	b, e := json.Marshal(v)
	if e != nil {
		return e
	}
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	s.mu.Lock()
	defer s.mu.Unlock()
	_, e = s.db.ExecContext(ctx, `INSERT INTO commands VALUES(?,?,?,?) ON CONFLICT(key) DO UPDATE SET state=excluded.state,data=excluded.data,updated_ms=excluded.updated_ms`, key, state, string(b), time.Now().UnixMilli())
	s.note(e)
	return e
}
func (s *Store) Recover() ([]json.RawMessage, error) {
	rows, e := s.db.Query(`SELECT data FROM commands WHERE state IN (1,2,3) ORDER BY updated_ms DESC LIMIT 128`)
	if e != nil {
		return nil, e
	}
	defer rows.Close()
	var out []json.RawMessage
	for rows.Next() {
		var b string
		if e = rows.Scan(&b); e != nil {
			return nil, e
		}
		out = append(out, json.RawMessage(b))
	}
	return out, rows.Err()
}
func (s *Store) Event(kind, message string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	_, e := s.db.Exec(`INSERT INTO events(time_ms,kind,message) VALUES(?,?,?)`, time.Now().UnixMilli(), kind, message)
	s.note(e)
	return e
}
func (s *Store) Events() ([]map[string]any, error) {
	ctx, c := context.WithTimeout(context.Background(), 3*time.Second)
	defer c()
	rows, e := s.db.QueryContext(ctx, `SELECT time_ms,kind,message FROM events ORDER BY id DESC LIMIT 100`)
	if e != nil {
		return nil, e
	}
	defer rows.Close()
	out := []map[string]any{}
	for rows.Next() {
		var ts int64
		var kind, msg string
		if e = rows.Scan(&ts, &kind, &msg); e != nil {
			return nil, e
		}
		out = append(out, map[string]any{"time_ms": ts, "kind": kind, "message": msg})
	}
	return out, rows.Err()
}
func (s *Store) Commands() ([]json.RawMessage, error) {
	ctx, c := context.WithTimeout(context.Background(), 3*time.Second)
	defer c()
	rows, e := s.db.QueryContext(ctx, "SELECT data FROM commands ORDER BY updated_ms DESC LIMIT 100")
	if e != nil {
		return nil, e
	}
	defer rows.Close()
	out := []json.RawMessage{}
	for rows.Next() {
		var raw string
		if e = rows.Scan(&raw); e != nil {
			return nil, e
		}
		out = append(out, json.RawMessage(raw))
	}
	return out, rows.Err()
}
func (s *Store) History(v []cache.View, recordMS int64) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	tx, e := s.db.BeginTx(ctx, nil)
	if e != nil {
		s.note(e)
		return e
	}
	defer tx.Rollback()
	q, e := tx.PrepareContext(ctx, `INSERT OR IGNORE INTO history(record_ms,slot,binding,read_ms,level,temp,density,avg_density,state,fault,quality,valid,level_ms,temp_ms,density_ms) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
	if e != nil {
		return e
	}
	defer q.Close()
	for _, m := range v {
		if m.Config.Slot == 0 || !m.Config.Enabled {
			continue
		}
		level := int32(uint32(m.Raw[0])<<16 | uint32(m.Raw[1]))
		if _, e = q.ExecContext(ctx, recordMS, m.Config.Slot, m.Config.Binding, m.FastCompleteMS, level, int16(m.Raw[26]), m.Raw[50], m.Raw[49], m.Raw[51], m.Raw[52], m.Quality, m.Valid, m.Groups[0].TimeMS, m.Groups[1].TimeMS, m.Groups[2].TimeMS); e != nil {
			s.note(e)
			return e
		}
	}
	e = tx.Commit()
	s.note(e)
	return e
}
func (s *Store) Cleanup(days, maxMB int, trusted ...bool) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	ctx, c := context.WithTimeout(context.Background(), 5*time.Second)
	defer c()
	for _, q := range []struct {
		SQL  string
		Args []any
	}{{`DELETE FROM history WHERE record_ms < ? LIMIT 5000`, []any{time.Now().Add(-time.Duration(days) * 24 * time.Hour).UnixMilli()}}, {`DELETE FROM events WHERE id NOT IN (SELECT id FROM events ORDER BY id DESC LIMIT 5000)`, nil}, {`DELETE FROM commands WHERE state NOT IN (1,2,3,6) AND updated_ms < ?`, []any{time.Now().Add(-30 * 24 * time.Hour).UnixMilli()}}} {
		if len(trusted) > 0 && !trusted[0] && len(q.Args) > 0 {
			continue
		}
		// SQLite builds do not all enable UPDATE_DELETE_LIMIT: use a primary-key subquery.
		if q.Args != nil && len(q.Args) > 0 && len(q.SQL) > 0 && q.SQL[:19] == "DELETE FROM history" {
			q.SQL = `DELETE FROM history WHERE (record_ms,slot) IN (SELECT record_ms,slot FROM history WHERE record_ms < ? ORDER BY record_ms LIMIT 5000)`
		}
		if _, e := s.db.ExecContext(ctx, q.SQL, q.Args...); e != nil {
			s.note(e)
			return e
		}
	}
	var pages, size, free int64
	_ = s.db.QueryRowContext(ctx, "PRAGMA page_count").Scan(&pages)
	_ = s.db.QueryRowContext(ctx, "PRAGMA page_size").Scan(&size)
	_ = s.db.QueryRowContext(ctx, "PRAGMA freelist_count").Scan(&free)
	if (pages-free)*size > int64(maxMB)*1024*1024 {
		_, e := s.db.ExecContext(ctx, `DELETE FROM history WHERE (record_ms,slot) IN (SELECT record_ms,slot FROM history ORDER BY record_ms LIMIT 5000)`)
		if e != nil {
			s.note(e)
			return e
		}
		s.errMu.Lock()
		s.capacityWarning = fmt.Sprintf("历史容量达到上限，已清理最早记录，实际保留期可能短于%d天", days)
		s.errMu.Unlock()
	}
	_, e := s.db.ExecContext(ctx, "PRAGMA wal_checkpoint(PASSIVE)")
	if e != nil {
		s.note(e)
	}
	return e
}
func (s *Store) Query(from, to, afterMS int64, afterSlot, slot, limit int) ([]Record, error) {
	ctx, c := context.WithTimeout(context.Background(), 3*time.Second)
	defer c()
	if limit < 1 || limit > 2000 {
		limit = 2000
	}
	rows, e := s.db.QueryContext(ctx, `SELECT record_ms,slot,binding,read_ms,level,temp,density,avg_density,state,fault,quality,valid,level_ms,temp_ms,density_ms FROM history WHERE record_ms>=? AND record_ms<=? AND (record_ms>? OR (record_ms=? AND slot>?)) AND (?=0 OR slot=?) ORDER BY record_ms,slot LIMIT ?`, from, to, afterMS, afterMS, afterSlot, slot, slot, limit)
	if e != nil {
		return nil, e
	}
	defer rows.Close()
	out := []Record{}
	for rows.Next() {
		var r Record
		if e = rows.Scan(&r.RecordMS, &r.Slot, &r.Binding, &r.ReadMS, &r.Level, &r.Temperature, &r.Density, &r.AverageDensity, &r.State, &r.Fault, &r.Quality, &r.Valid, &r.LevelMS, &r.TemperatureMS, &r.DensityMS); e != nil {
			return nil, e
		}
		out = append(out, r)
	}
	return out, rows.Err()
}

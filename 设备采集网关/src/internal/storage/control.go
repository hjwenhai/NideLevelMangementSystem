package storage

import (
	"context"
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/config"
	"time"
)

// ControlChange and its audit event are committed together, including stop cancellation.
type ControlChange struct {
	Slot          int
	Data          any
	ResolveLegacy bool
}

func (s *Store) SaveControls(changes []ControlChange, kind, message string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
	defer cancel()
	tx, err := s.db.BeginTx(ctx, nil)
	if err != nil {
		s.note(err)
		return err
	}
	defer tx.Rollback()
	for _, c := range changes {
		b, e := json.Marshal(c.Data)
		if e != nil {
			return e
		}
		if c.Slot < 1 || c.Slot > config.MaxMeters {
			return fmt.Errorf("invalid control slot")
		}
		if _, err = tx.ExecContext(ctx, `INSERT INTO control_slots(slot,data) VALUES(?,?) ON CONFLICT(slot) DO UPDATE SET data=excluded.data`, c.Slot, string(b)); err != nil {
			s.note(err)
			return err
		}
	}
	if kind == "control_maintenance_reset" || kind == "control_reset" {
		for _, c := range changes {
			if !c.ResolveLegacy {
				continue
			}
			if _, err = tx.ExecContext(ctx, `UPDATE commands SET state=7,data=json_set(data,'$.state',7,'$.error',?),updated_ms=? WHERE state IN(1,2,3,6) AND json_extract(data,'$.request.slot')=?`, "V3人工核查复位；见"+kind+"审计", time.Now().UnixMilli(), c.Slot); err != nil {
				s.note(err)
				return err
			}
		}
	}
	_, err = tx.ExecContext(ctx, `INSERT INTO events(time_ms,kind,message) VALUES(?,?,?)`, time.Now().UnixMilli(), kind, message)
	if err == nil {
		err = tx.Commit()
	}
	s.note(err)
	return err
}
func (s *Store) LoadControls() (map[int]json.RawMessage, error) {
	rows, err := s.db.Query(`SELECT slot,data FROM control_slots ORDER BY slot`)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	out := map[int]json.RawMessage{}
	for rows.Next() {
		var slot int
		var data string
		if err = rows.Scan(&slot, &data); err != nil {
			return nil, err
		}
		out[slot] = json.RawMessage(data)
	}
	return out, rows.Err()
}

// V2 unresolved/uncertain records must be inspected before V3 controls are opened.
func (s *Store) LegacyUnresolved() ([]json.RawMessage, error) {
	rows, e := s.db.Query(`SELECT data FROM commands WHERE state IN (1,2,3,6)`)
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

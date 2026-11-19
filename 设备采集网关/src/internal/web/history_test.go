package web

import (
	"encoding/csv"
	"encoding/json"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/storage"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"
)

func TestCSVSlotFilterPaginationAndBindingNames(t *testing.T) {
	cfg := config.Default(2)
	cfg.Meters[0].Name = "储罐A，液位计"
	cfg.Meters[1].Name = "储罐A，液位计" // Duplicate names must not merge slots.
	cfg.Meters[1].Enabled = false
	var records []storage.Record
	for i := 0; i < 2001; i++ {
		for slot := 1; slot <= 2; slot++ {
			binding := uint32(1)
			if i == 2000 && slot == 1 {
				binding = 2
			}
			records = append(records, storage.Record{RecordMS: int64(1000 + i), Slot: slot, Binding: binding, Level: 12345, Valid: 127})
		}
	}
	records = append(records, storage.Record{RecordMS: 4000, Slot: 3, Binding: 1})
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Header.Get("Authorization") != "Bearer test-token" {
			t.Error("missing internal token")
		}
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == "/api/config" {
			json.NewEncoder(w).Encode(cfg)
			return
		}
		if r.URL.Path != "/api/history" {
			t.Error(r.URL.Path)
			http.NotFound(w, r)
			return
		}
		q := r.URL.Query()
		filter, err := strconv.Atoi(q.Get("slot"))
		if err != nil {
			t.Error("export omitted filter on a page")
		}
		after, _ := strconv.ParseInt(q.Get("after"), 10, 64)
		last, _ := strconv.Atoi(q.Get("after_slot"))
		rows := []storage.Record{}
		for _, record := range records {
			if filter != 0 && record.Slot != filter || record.RecordMS < after || record.RecordMS == after && record.Slot <= last {
				continue
			}
			rows = append(rows, record)
			if len(rows) == 2000 {
				break
			}
		}
		json.NewEncoder(w).Encode(rows)
	}))
	defer upstream.Close()
	token := filepath.Join(t.TempDir(), "token")
	if err := os.WriteFile(token, []byte("test-token"), 0600); err != nil {
		t.Fatal(err)
	}
	s := &Server{base: upstream.URL, client: upstream.Client(), tokenFile: token, export: make(chan struct{}, 1)}
	for _, tc := range []struct {
		query       string
		slot, count int
	}{
		{"", 0, 4003}, {"&slot=0", 0, 4003}, {"&slot=1", 1, 2001}, {"&slot=2", 2, 2001}, {"&slot=3", 3, 1}, {"&slot=32", 32, 0},
	} {
		t.Run(tc.query, func(t *testing.T) {
			w := httptest.NewRecorder()
			s.csv(w, httptest.NewRequest("GET", "/api/history.csv?from=1000&to=100000"+tc.query, nil))
			if w.Code != 200 {
				t.Fatal(w.Code, w.Body.String())
			}
			rows, err := csv.NewReader(strings.NewReader(strings.TrimPrefix(w.Body.String(), "\ufeff"))).ReadAll()
			if err != nil || len(rows) != tc.count+1 || len(rows[0]) != 17 {
				t.Fatal("missing/duplicate rows", len(rows), err)
			}
			seen := make(map[string]bool)
			for _, row := range rows[1:] {
				if tc.slot != 0 && row[1] != strconv.Itoa(tc.slot) {
					t.Fatal("export leaked another slot", row)
				}
				key := row[0] + "/" + row[1]
				if seen[key] {
					t.Fatal("pagination duplicate", key)
				}
				seen[key] = true
				if row[1] == "3" || row[2] == "2" {
					if row[15] != "" {
						t.Fatal("current name attached to removed/different binding", row)
					}
				} else if row[15] != cfg.Meters[0].Name {
					t.Fatal("name missing", row)
				}
			}
			if tc.slot == 1 && !strings.Contains(w.Header().Get("Content-Disposition"), "slot-01.csv") {
				t.Fatal("filename missing slot")
			}
		})
	}
	for _, query := range []string{"&slot=-1", "&slot=65", "&slot=bad", "&slot=1&slot=2", "&slot="} {
		w := httptest.NewRecorder()
		s.csv(w, httptest.NewRequest("GET", "/api/history.csv?from=1000&to=100000"+query, nil))
		if w.Code != 400 {
			t.Fatal("invalid filter fell back to all", query, w.Code)
		}
	}
}

func TestCSVNameIsText(t *testing.T) {
	for _, name := range []string{"=HYPERLINK(\"example\")", "+1", "-2", "@SUM(1)", " \t=1", "\t罐A", "\r罐A", "\n罐A"} {
		if csvName(name) != "'"+name {
			t.Fatal("formula-like name not escaped", name)
		}
	}
	for _, name := range []string{"罐1，液位计", "液位计\"A\"", "1号罐", ""} {
		if csvName(name) != name {
			t.Fatal("ordinary name changed", name)
		}
	}
}

func TestCSVConfigFailureDoesNotProduceMislabeledFile(t *testing.T) {
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { http.Error(w, "unavailable", 500) }))
	defer upstream.Close()
	token := filepath.Join(t.TempDir(), "token")
	os.WriteFile(token, []byte("test"), 0600)
	s := &Server{base: upstream.URL, client: upstream.Client(), tokenFile: token, export: make(chan struct{}, 1)}
	w := httptest.NewRecorder()
	s.csv(w, httptest.NewRequest("GET", "/api/history.csv?from=1000&to=2000&slot=1", nil))
	if w.Code != 503 || strings.Contains(w.Header().Get("Content-Type"), "text/csv") {
		t.Fatal("false CSV success", w.Code)
	}
}

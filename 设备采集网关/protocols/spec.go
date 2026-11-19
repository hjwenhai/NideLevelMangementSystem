package protocols

import (
	"embed"
	"encoding/json"
	"fmt"
)

//go:embed north-v3.json
var files embed.FS

type Region struct {
	Name   string `json:"name"`
	Base   int    `json:"base"`
	Stride int    `json:"stride"`
	Count  int    `json:"count"`
	Access string `json:"access"`
}
type Field struct {
	Name   string `json:"name"`
	Offset int    `json:"offset"`
	Words  int    `json:"words"`
	Type   string `json:"type"`
	Unit   string `json:"unit"`
}
type Group struct {
	Name  string `json:"name"`
	Start uint16 `json:"start"`
	Words uint16 `json:"words"`
}
type Spec struct {
	Version     int            `json:"version"`
	Capacity    int            `json:"capacity"`
	Regions     []Region       `json:"regions"`
	Fields      []Field        `json:"summary_fields"`
	Groups      []Group        `json:"groups"`
	QualityBits map[string]int `json:"quality_bits"`
	ValidBits   map[string]int `json:"valid_bits"`
}

func Load() Spec {
	b, _ := files.ReadFile("north-v3.json")
	var s Spec
	if e := json.Unmarshal(b, &s); e != nil {
		panic(e)
	}
	if e := s.Validate(); e != nil {
		panic(e)
	}
	return s
}
func (s Spec) Region(name string) Region {
	for _, r := range s.Regions {
		if r.Name == name {
			return r
		}
	}
	panic("unknown region " + name)
}
func (s Spec) Validate() error {
	if s.Version != 3 || s.Capacity != Capacity {
		return fmt.Errorf("expected V3, 64 slots")
	}
	var covered [65536]bool
	for _, r := range s.Regions {
		if r.Base < 0 || r.Stride < 1 || r.Count < 1 || r.Base+r.Stride*r.Count > 65536 {
			return fmt.Errorf("invalid region %s", r.Name)
		}
		for a := r.Base; a < r.Base+r.Stride*r.Count; a++ {
			if covered[a] {
				return fmt.Errorf("region overlap at %d", a)
			}
			covered[a] = true
		}
	}
	width := s.Region("summary").Stride
	seen := make([]bool, width)
	for _, f := range s.Fields {
		if f.Offset < 0 || f.Words < 1 || f.Offset+f.Words > width {
			return fmt.Errorf("invalid field %s", f.Name)
		}
		for a := f.Offset; a < f.Offset+f.Words; a++ {
			if seen[a] {
				return fmt.Errorf("field overlap")
			}
			seen[a] = true
		}
	}
	return nil
}

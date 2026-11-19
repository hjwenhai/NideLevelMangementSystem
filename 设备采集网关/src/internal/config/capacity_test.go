package config

import (
	"encoding/json"
	"reflect"
	"testing"
)

func TestUpgrade32KeepsEveryBindingAndPolicy(t *testing.T) {
	old := Default(32)
	old.Schema = 2
	old.SlotVersions = old.SlotVersions[:32]
	// Reproduce a deployed four-by-eight layout, with non-default identities.
	for i := range old.Ports {
		old.Ports[i].Role = South
		old.Ports[i].Baud = 4800
	}
	for i := range old.Meters {
		old.Meters[i].Port = i/8 + 1
		old.Meters[i].Address = uint8(i%8 + 11)
		old.Meters[i].Binding = uint32(i + 7)
		old.SlotVersions[i] = uint32(i + 7)
	}
	old.TCP.AllowWrites = true
	old.Actions = []uint16{2, 4}
	old.Revision = 19
	b, _ := json.Marshal(old)
	got, err := Parse(b)
	if err != nil {
		t.Fatal(err)
	}
	if got.Schema != 3 || len(got.SlotVersions) != 64 || !reflect.DeepEqual(got.Meters, old.Meters) || !reflect.DeepEqual(got.Ports, old.Ports) || got.Revision != old.Revision || !reflect.DeepEqual(got.TCP, old.TCP) || !reflect.DeepEqual(got.Actions, old.Actions) {
		t.Fatal("migration changed field identities or permissions", got)
	}
	for i := 32; i < 64; i++ {
		if got.SlotVersions[i] != 1 {
			t.Fatal("new slot version")
		}
	}
	old.Schema = 1
	b, _ = json.Marshal(old)
	if _, e := Parse(b); e == nil {
		t.Fatal("legacy schema1 guessed")
	}
	old.Schema = 2
	old.SlotVersions = append(old.SlotVersions, 1)
	b, _ = json.Marshal(old)
	if _, e := Parse(b); e == nil {
		t.Fatal("malformed schema2 accepted")
	}
}

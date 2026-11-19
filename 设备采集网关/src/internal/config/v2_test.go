package config

import "testing"

func TestV2PortCapacityAndOldConfigRejection(t *testing.T) {
	for _, n := range []int{32, 48, 64} {
		c := Default(n)
		if e := c.Validate(); e != nil {
			t.Fatal(n, e)
		}
		counts := map[int]int{}
		south, north := 0, 0
		for _, m := range c.Meters {
			counts[m.Port]++
		}
		for _, p := range c.Ports {
			if p.Role == South {
				south++
			}
			if p.Role == North {
				north++
			}
			if counts[p.ID] > 16 {
				t.Fatal("port overflow")
			}
		}
		if n == 32 && (south != 2 || north != 1) || n == 48 && (south != 3 || north != 1) || n == 64 && (south != 4 || north != 0) {
			t.Fatal(n, south, north)
		}
	}
	c := Default(32)
	c.Schema = 1
	if c.Validate() == nil {
		t.Fatal("old schema accepted")
	}
	c = Default(32)
	c.SlotVersions = append(c.SlotVersions, 1)
	if c.Validate() == nil {
		t.Fatal("old capacity accepted")
	}
	c = Default(32)
	c.Meters[16].Port = 1
	c.Meters[16].Address = 17
	if c.Validate() == nil {
		t.Fatal("seventeenth enabled meter accepted")
	}
	c = Default(32)
	c.Meters[31].Slot = 65
	c.Meters[31].Enabled = false
	if c.Validate() == nil {
		t.Fatal("disabled slot65 accepted")
	}
	c = Default(24)
	c.Meters[0].Address = 243
	if c.Validate() != nil {
		t.Fatal("device address range incorrectly reduced to 16")
	}
	// Two south + two north remains valid but only supports 32 enabled meters.
	c = Default(16)
	c.Ports[3].Role = North
	if c.Validate() != nil {
		t.Fatal("two north roles rejected")
	}
}

package config

import (
	"encoding/json"
	"errors"
	"fmt"
	"path/filepath"
	"testing"
)

func TestConfigurations(t *testing.T) {
	for _, n := range []int{1, 8, 16, 24, 32, 48, 64} {
		c := Default(n)
		if e := c.Validate(); e != nil {
			t.Fatalf("%d: %v", n, e)
		}
	}
	c := Default(16)
	c.Ports[3].Role = North
	if e := c.Validate(); e != nil {
		t.Fatal(e)
	}
	cases := []struct {
		name   string
		change func(*Config)
	}{{"duplicate address", func(c *Config) { c.Meters[1].Address = c.Meters[0].Address }}, {"17 on bus", func(c *Config) { c.Meters[16].Port = 1; c.Meters[16].Address = 17 }}, {"meter on disabled", func(c *Config) { c.Ports[3].Role = Disabled }}, {"8N1 south", func(c *Config) { c.Ports[0].Parity = "N" }}, {"too fast south", func(c *Config) { c.Ports[0].Baud = 115200 }}, {"no north", func(c *Config) { c.Ports[2].Role = Disabled; c.TCP.Enabled = false }}, {"invalid slot", func(c *Config) { c.Meters[0].Slot = 65 }}, {"duplicate port", func(c *Config) { c.Ports[3].ID = 3 }}, {"multi action", func(c *Config) { c.Actions = []uint16{6} }}}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := Default(64)
			tc.change(&c)
			if c.Validate() == nil {
				t.Fatal("invalid configuration accepted")
			}
		})
	}
}
func TestPostRenameDurabilityError(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.json")
	old := syncDir
	syncDir = func(string) error { return fmt.Errorf("injected directory sync failure") }
	defer func() { syncDir = old }()
	err := SaveAtomic(path, Default(1))
	var committed *CommitError
	if !errors.As(err, &committed) {
		t.Fatal("post-rename error not classified", err)
	}
	if _, err = Load(path); err != nil {
		t.Fatal("renamed file missing", err)
	}
}
func TestParseAndAtomic(t *testing.T) {
	c := Default(32)
	b, _ := json.Marshal(c)
	if _, e := Parse(append(b, []byte(" {}")...)); e == nil {
		t.Fatal("trailing object accepted")
	}
	path := filepath.Join(t.TempDir(), "config.json")
	if e := SaveAtomic(path, c); e != nil {
		t.Fatal(e)
	}
	c.Revision++
	if e := SaveAtomic(path, c); e != nil {
		t.Fatal(e)
	}
	v, e := Load(path)
	if e != nil || v.Revision != 2 {
		t.Fatalf("%+v %v", v, e)
	}
}

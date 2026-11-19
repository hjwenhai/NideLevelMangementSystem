package protocols

import "testing"

func TestLayout(t *testing.T) {
	s := Load()
	if e := s.Validate(); e != nil {
		t.Fatal(e)
	}
	r := s.Region("summary")
	if r.Count != 64 || r.Stride != 24 || r.Base != 1000 {
		t.Fatal("summary mapping changed")
	}
	if s.Region("detail").Base+s.Region("detail").Stride*64 > s.Region("level_array").Base {
		t.Fatal("overlap")
	}
	if s.Region("simple_control").Stride != 12 {
		t.Fatal("control size")
	}
}

package platform

import (
	"bytes"
	"errors"
	"io"
	"testing"
)

type testLED struct {
	bytes.Buffer
	closed bool
	fail   bool
}

func (f *testLED) Close() error { f.closed = true; return nil }
func (f *testLED) Write(p []byte) (int, error) {
	if f.fail {
		return 0, errors.New("write failed")
	}
	return f.Buffer.Write(p)
}
func TestLEDPathsAndASCIICommands(t *testing.T) {
	var paths []string
	var files []*testLED
	d, e := openLEDs(func(p string) (io.WriteCloser, error) {
		paths = append(paths, p)
		f := &testLED{}
		files = append(files, f)
		return f, nil
	})
	if e != nil || len(paths) != 3 || paths[0] != "/dev/led" || paths[1] != "/dev/led2" || paths[2] != "/dev/led3" {
		t.Fatal(paths, e)
	}
	if e = d.Set([3]bool{true, false, true}); e != nil {
		t.Fatal(e)
	}
	_ = d.Set([3]bool{true, false, true}) // unchanged lamps do not generate extra writes
	if files[0].String() != "1" || files[1].String() != "0" || files[2].String() != "1" {
		t.Fatal("wrong ASCII or repeated writes")
	}
	files[1].fail = true
	if e = d.Set([3]bool{true, true, true}); e == nil {
		t.Fatal("lost driver error")
	}
	d.Close()
	for _, f := range files {
		if !f.closed {
			t.Fatal("descriptor leaked")
		}
	}
}
func TestLEDPartialOpenClosesEarlierDevices(t *testing.T) {
	f := &testLED{}
	if _, e := openLEDs(func(p string) (io.WriteCloser, error) {
		if p == "/dev/led" {
			return f, nil
		}
		return nil, errors.New("missing")
	}); e == nil || !f.closed {
		t.Fatal("partial open leaked")
	}
}

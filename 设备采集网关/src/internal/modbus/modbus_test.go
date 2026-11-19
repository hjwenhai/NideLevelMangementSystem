package modbus

import (
	"bytes"
	"encoding/binary"
	"testing"
)

func TestCRCAndFrames(t *testing.T) {
	p := []byte{1, 3, 0, 0, 0, 10}
	if CRC(p) != 0xcdc5 {
		t.Fatalf("CRC=%04x", CRC(p))
	}
	f := Frame(1, p[1:])
	unit, out, e := CheckFrame(f)
	if e != nil || unit != 1 || !bytes.Equal(out, p[1:]) {
		t.Fatal(e)
	}
	f[4] ^= 1
	if _, _, e = CheckFrame(f); e == nil {
		t.Fatal("bad CRC accepted")
	}
}
func TestPDUBoundaries(t *testing.T) {
	reads := 0
	read := func(_ byte, a, n uint16) ([]uint16, error) {
		reads++
		w := make([]uint16, n)
		w[0] = 0xffff
		return w, nil
	}
	writes := 0
	write := func(_ uint16, _ []uint16) error { writes++; return nil }
	for _, n := range []uint16{0, 126, 65535} {
		p := Handle(ReadPDU(4, 0, n), read, write)
		if p[0] != 0x84 || p[1] != 3 {
			t.Fatal(p)
		}
	}
	if reads != 0 {
		t.Fatal("invalid PDU reached reader")
	}
	p := Handle(ReadPDU(4, 0, 125), read, write)
	w, e := DecodeWords(p, 125)
	if e != nil || w[0] != 65535 || len(w) != 125 {
		t.Fatal(e)
	}
	p = Handle(ReadPDU(4, 65535, 2), read, write)
	if p[1] != 3 {
		t.Fatal(p)
	}
	good := WritePDU(40000, make([]uint16, 64))
	bad := append([]byte(nil), good...)
	bad[5] = 1
	p = Handle(bad, read, write)
	if p[1] != 3 || writes != 0 {
		t.Fatal(p)
	}
	p = Handle(good, read, write)
	if writes != 1 || len(p) != 5 || binary.BigEndian.Uint16(p[3:]) != 64 {
		t.Fatal(p)
	}
}
func FuzzHandle(f *testing.F) {
	f.Add([]byte{4, 0, 0, 0, 1})
	f.Add([]byte{16, 0, 0, 0, 1, 2, 0, 0})
	f.Fuzz(func(t *testing.T, p []byte) {
		_ = Handle(p, func(_ byte, _ uint16, n uint16) ([]uint16, error) { return make([]uint16, n), nil }, func(_ uint16, _ []uint16) error { return nil })
	})
}

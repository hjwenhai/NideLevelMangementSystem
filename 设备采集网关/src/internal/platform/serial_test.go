package platform

import (
	"context"
	"io"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"net"
	"testing"
	"time"
)

type virtualPort struct{ net.Conn }

func (p *virtualPort) Read(b []byte) (int, error) {
	_ = p.SetReadDeadline(time.Now().Add(time.Millisecond))
	n, e := p.Conn.Read(b)
	if timeout, ok := e.(net.Error); ok && timeout.Timeout() {
		return 0, nil
	}
	return n, e
}
func TestRTUGapIncludesParityAndStopBits(t *testing.T) {
	for _, tc := range []struct {
		baud   int
		parity string
		stop   int
		gap    time.Duration
	}{{1200, "E", 1, 32083333}, {1200, "N", 1, 29166666}, {1200, "E", 2, 35000000}, {38400, "E", 2, 1750000}} {
		s := Serial{Config: config.Port{Baud: tc.baud, Parity: tc.parity, StopBits: tc.stop}}
		if actual := s.gap(); actual != tc.gap {
			t.Fatalf("%+v: gap=%v", tc, actual)
		}
	}
}
func (p *virtualPort) Drain() error            { return nil }
func (p *virtualPort) ResetInputBuffer() error { return nil }
func TestRTUMasterFC06PasswordEcho(t *testing.T) {
	for _, bad := range []bool{false, true} {
		server, client := net.Pipe()
		s := &Serial{Port: &virtualPort{client}, Config: config.Port{Baud: 4800, Parity: "E", StopBits: 1, TimeoutMS: 100}}
		done := make(chan struct{})
		go func() {
			defer close(done)
			defer server.Close()
			frame := make([]byte, 8)
			if _, e := io.ReadFull(server, frame); e != nil {
				return
			}
			if bad {
				frame[5] ^= 1
				frame = modbus.Frame(1, frame[1:6])
			}
			modbus.WriteAll(server, frame)
		}()
		err := WriteSingleRegister(context.Background(), s, 1, 129, 0xea66)
		client.Close()
		<-done
		if (err != nil) != bad {
			t.Fatalf("bad=%t error=%v", bad, err)
		}
	}
}
func TestRTUSlaveCRCAndBroadcast(t *testing.T) {
	server, client := net.Pipe()
	defer client.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	s := &Serial{Port: &virtualPort{server}, Config: config.Port{ID: 3, UnitID: 1, Baud: 9600}}
	calls := 0
	done := make(chan struct{})
	go func() {
		defer close(done)
		_ = s.Serve(ctx, func(_ string, _ byte, p []byte) []byte {
			calls++
			return modbus.Handle(p, func(_ byte, _ uint16, n uint16) ([]uint16, error) { return []uint16{0x1234}, nil }, func(_ uint16, _ []uint16) error { return nil })
		})
	}()
	for _, bad := range [][]byte{modbus.Frame(0, modbus.ReadPDU(4, 0, 1)), modbus.Frame(2, modbus.ReadPDU(4, 0, 1))} {
		if _, e := client.Write(bad); e != nil {
			t.Fatal(e)
		}
		_ = client.SetReadDeadline(time.Now().Add(15 * time.Millisecond))
		if _, e := client.Read(make([]byte, 20)); e == nil {
			t.Fatal("unexpected reply")
		}
	}
	good := modbus.Frame(1, modbus.ReadPDU(4, 0, 1))
	bad := append([]byte(nil), good...)
	bad[len(bad)-1] ^= 1
	_, _ = client.Write(bad)
	_ = client.SetReadDeadline(time.Now().Add(15 * time.Millisecond))
	_, _ = client.Read(make([]byte, 20))
	_, _ = client.Write(good)
	_ = client.SetReadDeadline(time.Now().Add(time.Second))
	buf := make([]byte, 7)
	if _, e := io.ReadFull(client, buf); e != nil {
		t.Fatal(e)
	}
	_, p, e := modbus.CheckFrame(buf)
	if e != nil || p[2] != 0x12 || p[3] != 0x34 {
		t.Fatalf("%x %v", buf, e)
	}
	cancel()
	s.Close()
	<-done
	if calls != 1 {
		t.Fatal(calls)
	}
}

func TestRTUFC06EchoAndMalformedNoWrite(t *testing.T) {
	server, client := net.Pipe()
	defer client.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	s := &Serial{Port: &virtualPort{server}, Config: config.Port{ID: 3, UnitID: 1, Baud: 38400, Parity: "E", StopBits: 1}}
	writes := 0
	done := make(chan struct{})
	go func() {
		defer close(done)
		s.Serve(ctx, func(_ string, _ byte, p []byte) []byte {
			return modbus.Handle(p, func(_ byte, _ uint16, _ uint16) ([]uint16, error) { return []uint16{0}, nil }, func(a uint16, w []uint16) error {
				if a != 30000 || len(w) != 1 || w[0] != 5 {
					t.Error("FC06 decoded incorrectly")
				}
				writes++
				return nil
			})
		})
	}()
	pdu := []byte{6, 0x75, 0x30, 0, 5}
	frame := modbus.Frame(1, pdu)
	if _, err := client.Write(frame); err != nil {
		t.Fatal(err)
	}
	client.SetReadDeadline(time.Now().Add(time.Second))
	reply := make([]byte, 8)
	if _, err := io.ReadFull(client, reply); err != nil {
		t.Fatal(err)
	}
	for i := range frame {
		if reply[i] != frame[i] {
			t.Fatal("FC06 RTU echo")
		}
	}
	bad := modbus.Frame(1, []byte{6, 0x75, 0x30, 0})
	client.Write(bad)
	client.SetReadDeadline(time.Now().Add(time.Second))
	reply = make([]byte, 5)
	if _, err := io.ReadFull(client, reply); err != nil {
		t.Fatal(err)
	}
	_, p, err := modbus.CheckFrame(reply)
	if err != nil || p[0] != 0x86 || p[1] != 3 {
		t.Fatal("malformed FC06 accepted")
	}
	cancel()
	s.Close()
	<-done
	if writes != 1 {
		t.Fatal("malformed request wrote state")
	}
}

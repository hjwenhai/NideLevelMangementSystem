package platform

import (
	"bytes"
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"go.bug.st/serial"
	"io"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"time"
)

type Wire interface {
	Exchange(context.Context, byte, []byte) ([]byte, error)
	Close() error
}
type SerialIO interface {
	io.ReadWriteCloser
	Drain() error
	ResetInputBuffer() error
}
type Serial struct {
	Port   SerialIO
	Config config.Port
	last   time.Time
}

func Open(p config.Port) (*Serial, error) {
	parity := serial.EvenParity
	if p.Parity == "N" {
		parity = serial.NoParity
	}
	if p.Parity == "O" {
		parity = serial.OddParity
	}
	stop := serial.OneStopBit
	if p.StopBits == 2 {
		stop = serial.TwoStopBits
	}
	v, e := serial.Open(fmt.Sprintf("/dev/ttyS%d", p.ID), &serial.Mode{BaudRate: p.Baud, DataBits: 8, Parity: parity, StopBits: stop})
	if e != nil {
		return nil, e
	}
	if e = v.SetReadTimeout(time.Millisecond); e != nil {
		v.Close()
		return nil, e
	}
	return &Serial{Port: v, Config: p}, nil
}
func (s *Serial) Close() error { return s.Port.Close() }
func (s *Serial) gap() time.Duration {
	if s.Config.Baud > 19200 {
		return 1750 * time.Microsecond
	}
	bits := 1 + 8 + s.Config.StopBits
	if s.Config.Parity != "N" {
		bits++
	}
	return time.Duration(float64(time.Second) * float64(bits) * 3.5 / float64(s.Config.Baud))
}
func (s *Serial) wait(ctx context.Context) error {
	wait := s.gap() - time.Since(s.last)
	if wait <= 0 {
		return ctx.Err()
	}
	t := time.NewTimer(wait)
	defer t.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-t.C:
		return nil
	}
}
func (s *Serial) Exchange(ctx context.Context, unit byte, pdu []byte) ([]byte, error) {
	if e := s.wait(ctx); e != nil {
		return nil, e
	}
	if e := s.Port.ResetInputBuffer(); e != nil {
		return nil, e
	}
	if e := modbus.WriteAll(s.Port, modbus.Frame(unit, pdu)); e != nil {
		return nil, e
	}
	if e := s.Port.Drain(); e != nil {
		return nil, e
	}
	s.last = time.Now()
	until := time.Now().Add(time.Duration(s.Config.TimeoutMS) * time.Millisecond)
	b := make([]byte, 0, 256)
	tmp := make([]byte, 256)
	for time.Now().Before(until) {
		if e := ctx.Err(); e != nil {
			return nil, e
		}
		n, e := s.Port.Read(tmp)
		if e != nil {
			return nil, e
		}
		if n == 0 {
			continue
		}
		s.last = time.Now()
		b = append(b, tmp[:n]...)
		if len(b) > 256 {
			return nil, errors.New("RTU响应过长")
		}
		if len(b) < 3 {
			continue
		}
		size := 0
		if b[1]&128 != 0 {
			size = 5
		} else if b[1] == 3 || b[1] == 4 {
			size = 5 + int(b[2])
		} else if b[1] == 16 || b[1] == 6 {
			size = 8
		}
		if size < 4 || len(b) < size {
			continue
		}
		if len(b) != size {
			return nil, errors.New("RTU响应长度不匹配")
		}
		u, r, e := modbus.CheckFrame(b)
		if e != nil {
			return nil, e
		}
		if u != unit || r[0] != (pdu[0]) && r[0] != (pdu[0]|128) {
			return nil, errors.New("响应站号/功能码不匹配")
		}
		if r[0]&128 != 0 {
			return nil, modbus.Exception(r[1])
		}
		if (pdu[0] == 16 || pdu[0] == 6) && !bytes.Equal(r, pdu[:5]) {
			return nil, errors.New("写应答地址/数量不匹配")
		}
		return r, nil
	}
	return nil, context.DeadlineExceeded
}
func (s *Serial) Serve(ctx context.Context, handler modbus.Handler) error {
	buf := make([]byte, 0, 256)
	tmp := make([]byte, 256)
	last := time.Time{}
	dispatch := func() {
		u, p, e := modbus.CheckFrame(buf)
		buf = buf[:0]
		if e != nil || u == 0 || u != s.Config.UnitID {
			return
		}
		r := handler(fmt.Sprintf("rtu:COM%d", s.Config.ID), u, p)
		if len(r) == 0 {
			return
		}
		if s.wait(ctx) != nil {
			return
		}
		_ = modbus.WriteAll(s.Port, modbus.Frame(u, r))
		_ = s.Port.Drain()
		s.last = time.Now()
	}
	for ctx.Err() == nil {
		n, e := s.Port.Read(tmp)
		if e != nil {
			if ctx.Err() != nil {
				return nil
			}
			return e
		}
		if n == 0 {
			if len(buf) > 0 && !last.IsZero() && time.Since(last) >= s.gap() {
				dispatch()
			}
			continue
		}
		if len(buf) > 0 && time.Since(last) > s.gap() {
			dispatch()
		}
		last = time.Now()
		s.last = last
		buf = append(buf, tmp[:n]...)
		if len(buf) > 256 {
			buf = buf[:0]
			continue
		}
		if len(buf) >= 2 {
			size := 0
			if buf[1] == 3 || buf[1] == 4 || buf[1] == 6 {
				size = 8
			} else if buf[1] == 16 && len(buf) >= 7 {
				size = 9 + int(buf[6])
			}
			if size > 0 && len(buf) == size {
				dispatch()
			}
		}
	}
	return nil
}
func ReadRegisters(ctx context.Context, w Wire, unit byte, fc byte, start, count uint16) ([]uint16, error) {
	p, e := w.Exchange(ctx, unit, modbus.ReadPDU(fc, start, count))
	if e != nil {
		return nil, e
	}
	return modbus.DecodeWords(p, count)
}
func WriteRegisters(ctx context.Context, w Wire, unit byte, start uint16, words []uint16) error {
	p := modbus.WritePDU(start, words)
	r, e := w.Exchange(ctx, unit, p)
	if e != nil {
		return e
	}
	if len(r) != 5 || r[0] != 16 || binary.BigEndian.Uint16(r[1:]) != start || binary.BigEndian.Uint16(r[3:]) != uint16(len(words)) {
		return io.ErrUnexpectedEOF
	}
	return nil
}

// WriteSingleRegister uses FC06, as required by the remote level password flow.
func WriteSingleRegister(ctx context.Context, w Wire, unit byte, start, value uint16) error {
	p := []byte{6, 0, 0, 0, 0}
	binary.BigEndian.PutUint16(p[1:], start)
	binary.BigEndian.PutUint16(p[3:], value)
	r, err := w.Exchange(ctx, unit, p)
	if err != nil {
		return err
	}
	if !bytes.Equal(r, p) {
		return io.ErrUnexpectedEOF
	}
	return nil
}

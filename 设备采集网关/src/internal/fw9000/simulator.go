package fw9000

import (
	"context"
	"encoding/binary"
	"fmt"
	"levelgateway/src/internal/config"
	"levelgateway/src/internal/modbus"
	"sync"
	"time"
)

type Fault struct {
	DelayMS          int  `json:"delay_ms"`
	Offline          bool `json:"offline"`
	CRC              bool `json:"crc_error"`
	DropWrite        bool `json:"drop_write_reply"`
	UnsupportedStart int  `json:"unsupported_start"`
}
type SimMeter struct {
	IR               [53]uint16
	HR               map[uint16]uint16
	LevelPassword    uint16
	OffsetWriteCount int
	Authenticated    bool
	Fault            Fault
	ActionCount      int
}
type Simulator struct {
	mu     sync.Mutex
	Meters map[byte]*SimMeter
}

func NewSimulator() *Simulator {
	s := &Simulator{Meters: map[byte]*SimMeter{}}
	for i := 1; i <= config.MaxPerPort; i++ {
		m := &SimMeter{HR: map[uint16]uint16{0: 100, 130: uint16(i), 131: 0, 132: 3, 698: 1, 129: 0, 503: 0, 504: 0}, Fault: Fault{UnsupportedStart: -1}}
		value := int32(100000 + i*123)
		modbus.Put32(m.IR[:], uint32(value))
		m.IR[3] = uint16(i)
		m.IR[4] = uint16(value / 10)
		m.IR[5] = 5000
		m.IR[22] = 65535
		m.IR[23] = 65535
		m.IR[26] = uint16(2000 + i)
		m.IR[49] = 8500
		m.IR[50] = 8510
		m.IR[51] = 0x8000
		for a := 6; a <= 21; a++ {
			m.IR[a] = uint16(2000 + i + a)
		}
		for a := 33; a <= 48; a++ {
			m.IR[a] = 8500
		}
		for a := 3300; a <= 3337; a++ {
			m.HR[uint16(a)] = 0
		}
		s.Meters[byte(i)] = m
	}
	return s
}
func (s *Simulator) SetFault(unit byte, f Fault) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	m := s.Meters[unit]
	if m == nil {
		return fmt.Errorf("unknown unit")
	}
	m.Fault = f
	return nil
}
func (s *Simulator) SetRaw(unit byte, address int, value uint16) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.Meters[unit].IR[address] = value
}
func (s *Simulator) Count(unit byte) int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.Meters[unit].ActionCount
}
func (s *Simulator) Handle(unit byte, pdu []byte) []byte {
	s.mu.Lock()
	defer s.mu.Unlock()
	m := s.Meters[unit]
	if m == nil {
		return modbus.ErrorPDU(pdu[0], modbus.IllegalAddress)
	}
	return modbus.Handle(pdu, func(fc byte, start, count uint16) ([]uint16, error) {
		if fc == 4 {
			if int(start)+int(count) > 53 || start <= 32 && int(start)+int(count) > 31 {
				return nil, modbus.IllegalAddress
			}
			if int(start) == m.Fault.UnsupportedStart {
				return nil, modbus.IllegalAddress
			}
			return append([]uint16(nil), m.IR[start:int(start)+int(count)]...), nil
		}
		w := make([]uint16, count)
		for i := range w {
			a := start + uint16(i)
			v, ok := m.HR[a]
			if !ok || a == 699 {
				return nil, modbus.IllegalAddress
			}
			w[i] = v
		}
		return w, nil
	}, func(start uint16, words []uint16) error {
		if start == 503 && len(words) == 2 && pdu[0] == 16 {
			if m.LevelPassword != 0xea66 {
				return modbus.DeviceFailure
			}
			old := int32(uint32(m.HR[503])<<16 | uint32(m.HR[504]))
			next := int32(uint32(words[0])<<16 | uint32(words[1]))
			if next < -99999 || next > 999999 {
				return modbus.IllegalValue
			}
			level := int32(uint32(m.IR[0])<<16 | uint32(m.IR[1]))
			modbus.Put32(m.IR[:2], uint32(level+next-old))
			m.HR[503] = words[0]
			m.HR[504] = words[1]
			m.OffsetWriteCount++
			return nil
		}
		if len(words) != 1 {
			return modbus.IllegalValue
		}
		if start == 129 {
			if words[0] == 0xea66 && pdu[0] != 6 {
				return modbus.IllegalValue
			}
			m.LevelPassword = words[0]
			m.HR[129] = words[0]
			m.Authenticated = words[0] == 0x10d9
			return nil
		}
		if start != 699 {
			return modbus.IllegalAddress
		}
		if !m.Authenticated {
			return modbus.DeviceFailure
		}
		m.Authenticated = false
		m.ActionCount++
		m.HR[698] = words[0]
		// Simulate remote acceptance, not mechanical completion or action-bit physics.
		m.IR[51] = 0x8000
		return nil
	})
}
func (s *Simulator) Exchange(ctx context.Context, unit byte, pdu []byte) ([]byte, error) {
	s.mu.Lock()
	m := s.Meters[unit]
	f := Fault{Offline: true}
	if m != nil {
		f = m.Fault
	}
	s.mu.Unlock()
	d := time.Duration(f.DelayMS) * time.Millisecond
	if f.Offline {
		d = time.Second
	}
	if d > 0 {
		t := time.NewTimer(d)
		defer t.Stop()
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-t.C:
		}
	}
	if f.Offline {
		return nil, context.DeadlineExceeded
	}
	_, p, e := modbus.CheckFrame(modbus.Frame(unit, pdu))
	if e != nil {
		return nil, e
	}
	response := s.Handle(unit, p)
	if f.DropWrite && pdu[0] == 16 && binary.BigEndian.Uint16(pdu[1:]) == 699 {
		return nil, context.DeadlineExceeded
	}
	frame := modbus.Frame(unit, response)
	if f.CRC {
		frame[len(frame)-1] ^= 1
	}
	_, response, e = modbus.CheckFrame(frame)
	if e != nil {
		return nil, e
	}
	if response[0]&128 != 0 {
		return nil, modbus.Exception(response[1])
	}
	return response, nil
}
func (s *Simulator) Close() error { return nil }

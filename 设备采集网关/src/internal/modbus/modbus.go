package modbus

import (
	"context"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"net"
	"sync"
	"time"
)

type Exception uint8

func (e Exception) Error() string { return fmt.Sprintf("Modbus exception %d", e) }

const (
	IllegalFunction Exception = 1
	IllegalAddress  Exception = 2
	IllegalValue    Exception = 3
	DeviceFailure   Exception = 4
	Busy            Exception = 6
)

func CRC(b []byte) uint16 {
	crc := uint16(0xffff)
	for _, v := range b {
		crc ^= uint16(v)
		for i := 0; i < 8; i++ {
			if crc&1 != 0 {
				crc = crc>>1 ^ 0xa001
			} else {
				crc >>= 1
			}
		}
	}
	return crc
}
func Frame(unit byte, pdu []byte) []byte {
	b := append([]byte{unit}, pdu...)
	crc := CRC(b)
	return append(b, byte(crc), byte(crc>>8))
}
func CheckFrame(b []byte) (byte, []byte, error) {
	if len(b) < 4 || len(b) > 256 {
		return 0, nil, errors.New("RTU长度无效")
	}
	if CRC(b[:len(b)-2]) != binary.LittleEndian.Uint16(b[len(b)-2:]) {
		return 0, nil, errors.New("CRC不匹配")
	}
	return b[0], b[1 : len(b)-2], nil
}
func ReadPDU(fc byte, start, count uint16) []byte {
	b := make([]byte, 5)
	b[0] = fc
	binary.BigEndian.PutUint16(b[1:], start)
	binary.BigEndian.PutUint16(b[3:], count)
	return b
}
func WritePDU(start uint16, words []uint16) []byte {
	b := make([]byte, 6+2*len(words))
	b[0] = 16
	binary.BigEndian.PutUint16(b[1:], start)
	binary.BigEndian.PutUint16(b[3:], uint16(len(words)))
	b[5] = byte(len(words) * 2)
	for i, w := range words {
		binary.BigEndian.PutUint16(b[6+i*2:], w)
	}
	return b
}
func DecodeWords(pdu []byte, count uint16) ([]uint16, error) {
	if len(pdu) >= 2 && pdu[0]&128 != 0 {
		return nil, Exception(pdu[1])
	}
	if len(pdu) != 2+int(count)*2 || pdu[1] != byte(count*2) {
		return nil, errors.New("寄存器响应长度无效")
	}
	w := make([]uint16, count)
	for i := range w {
		w[i] = binary.BigEndian.Uint16(pdu[2+i*2:])
	}
	return w, nil
}

type Handler func(source string, unit byte, pdu []byte) []byte

func ErrorPDU(fc byte, err error) []byte {
	code := DeviceFailure
	var ex Exception
	if errors.As(err, &ex) {
		code = ex
	}
	return []byte{fc | 128, byte(code)}
}

// Handle validates complete PDUs before calling application operations.
func Handle(pdu []byte, read func(byte, uint16, uint16) ([]uint16, error), write func(uint16, []uint16) error) []byte {
	if len(pdu) == 0 {
		return nil
	}
	fc := pdu[0]
	if fc == 3 || fc == 4 {
		if len(pdu) != 5 {
			return ErrorPDU(fc, IllegalValue)
		}
		a, n := binary.BigEndian.Uint16(pdu[1:]), binary.BigEndian.Uint16(pdu[3:])
		if n < 1 || n > 125 || uint32(a)+uint32(n) > 65536 {
			return ErrorPDU(fc, IllegalValue)
		}
		w, e := read(fc, a, n)
		if e != nil {
			return ErrorPDU(fc, e)
		}
		b := []byte{fc, byte(n * 2)}
		for _, v := range w {
			b = append(b, byte(v>>8), byte(v))
		}
		return b
	}
	if fc == 6 {
		if len(pdu) != 5 {
			return ErrorPDU(fc, IllegalValue)
		}
		a, v := binary.BigEndian.Uint16(pdu[1:]), binary.BigEndian.Uint16(pdu[3:])
		if e := write(a, []uint16{v}); e != nil {
			return ErrorPDU(fc, e)
		}
		return append([]byte(nil), pdu...)
	}
	if fc == 16 {
		if len(pdu) < 6 {
			return ErrorPDU(fc, IllegalValue)
		}
		a, n := binary.BigEndian.Uint16(pdu[1:]), binary.BigEndian.Uint16(pdu[3:])
		if n < 1 || n > 123 || int(pdu[5]) != int(n)*2 || len(pdu) != 6+int(n)*2 || uint32(a)+uint32(n) > 65536 {
			return ErrorPDU(fc, IllegalValue)
		}
		w := make([]uint16, n)
		for i := range w {
			w[i] = binary.BigEndian.Uint16(pdu[6+i*2:])
		}
		if e := write(a, w); e != nil {
			return ErrorPDU(fc, e)
		}
		return append([]byte(nil), pdu[:5]...)
	}
	return ErrorPDU(fc, IllegalFunction)
}
func ServeTCP(ctx context.Context, l net.Listener, maxClients int, handler Handler) error {
	var wg sync.WaitGroup
	slots := make(chan struct{}, maxClients)
	go func() { <-ctx.Done(); l.Close() }()
	defer wg.Wait()
	for {
		c, e := l.Accept()
		if e != nil {
			if ctx.Err() != nil {
				return nil
			}
			return e
		}
		select {
		case slots <- struct{}{}:
		default:
			c.Close()
			continue
		}
		wg.Add(1)
		go func() {
			defer wg.Done()
			defer func() { <-slots }()
			defer c.Close()
			closed := make(chan struct{})
			go func() {
				select {
				case <-ctx.Done():
					c.Close()
				case <-closed:
				}
			}()
			defer close(closed)
			source := "tcp:" + c.RemoteAddr().String()
			for {
				c.SetReadDeadline(time.Now().Add(15 * time.Second))
				h := make([]byte, 7)
				if _, e := io.ReadFull(c, h); e != nil {
					return
				}
				n := binary.BigEndian.Uint16(h[4:])
				if binary.BigEndian.Uint16(h[2:]) != 0 || n < 2 || n > 254 {
					return
				}
				b := make([]byte, n-1)
				if _, e := io.ReadFull(c, b); e != nil {
					return
				}
				resp := handler(source, h[6], b)
				if len(resp) == 0 {
					continue
				}
				binary.BigEndian.PutUint16(h[4:], uint16(len(resp)+1))
				c.SetWriteDeadline(time.Now().Add(3 * time.Second))
				if e := WriteAll(c, append(h, resp...)); e != nil {
					return
				}
			}
		}()
	}
}
func WriteAll(w io.Writer, b []byte) error {
	for len(b) > 0 {
		n, e := w.Write(b)
		if e != nil {
			return e
		}
		if n == 0 {
			return io.ErrShortWrite
		}
		b = b[n:]
	}
	return nil
}
func Put32(w []uint16, v uint32) { w[0] = uint16(v >> 16); w[1] = uint16(v) }
func Put64(w []uint16, v uint64) { Put32(w, uint32(v>>32)); Put32(w[2:], uint32(v)) }
func Get32(w []uint16) uint32    { return uint32(w[0])<<16 | uint32(w[1]) }
func Get64(w []uint16) uint64    { return uint64(Get32(w))<<32 | uint64(Get32(w[2:])) }

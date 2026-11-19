package platform

import (
	"fmt"
	"io"
	"os"
	"runtime"
)

// Vendor Buildroot V1.3 p22: SYS1=/dev/led, SYS2=/dev/led2, SYS3=/dev/led3.
// Vendor Go sample writes ASCII '1' for on, '0' for off. Verify all three on board.
type LEDDriver struct {
	files [3]io.WriteCloser
	known [3]bool
	last  [3]bool
}

func OpenLEDs() (*LEDDriver, error) {
	if runtime.GOOS != "linux" {
		return nil, fmt.Errorf("SYS指示灯驱动仅支持Linux实机")
	}
	return openLEDs(func(path string) (io.WriteCloser, error) { return os.OpenFile(path, os.O_WRONLY, 0) })
}
func openLEDs(open func(string) (io.WriteCloser, error)) (*LEDDriver, error) {
	d := &LEDDriver{}
	for i, path := range []string{"/dev/led", "/dev/led2", "/dev/led3"} {
		f, err := open(path)
		if err != nil {
			d.Close()
			return nil, fmt.Errorf("%s: %w", path, err)
		}
		d.files[i] = f
	}
	return d, nil
}
func (d *LEDDriver) Set(on [3]bool) error {
	for i, f := range d.files {
		if f == nil {
			return fmt.Errorf("SYS%d未打开", i+1)
		}
		if d.known[i] && d.last[i] == on[i] {
			continue
		}
		b := byte('0')
		if on[i] {
			b = '1'
		}
		n, err := f.Write([]byte{b})
		if err != nil {
			return fmt.Errorf("SYS%d: %w", i+1, err)
		}
		if n != 1 {
			return io.ErrShortWrite
		}
		d.last[i], d.known[i] = on[i], true
	}
	return nil
}
func (d *LEDDriver) Close() {
	for i, f := range d.files {
		if f != nil {
			_, _ = f.Write([]byte{'0'})
			_ = f.Close()
			d.files[i] = nil
		}
	}
}

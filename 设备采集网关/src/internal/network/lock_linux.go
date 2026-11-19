package network

import (
	"golang.org/x/sys/unix"
	"os"
	"time"
)

func lockFile(path string) (func(), error) {
	f, e := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0600)
	if e != nil {
		return nil, e
	}
	deadline := time.Now().Add(3 * time.Second)
	for {
		e = unix.Flock(int(f.Fd()), unix.LOCK_EX|unix.LOCK_NB)
		if e == nil {
			return func() { _ = unix.Flock(int(f.Fd()), unix.LOCK_UN); _ = f.Close() }, nil
		}
		if time.Now().After(deadline) {
			f.Close()
			return nil, e
		}
		time.Sleep(10 * time.Millisecond)
	}
}

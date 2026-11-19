package network

import (
	"golang.org/x/sys/windows"
	"os"
	"time"
)

func lockFile(path string) (func(), error) {
	f, e := os.OpenFile(path, os.O_CREATE|os.O_RDWR, 0600)
	if e != nil {
		return nil, e
	}
	over := &windows.Overlapped{}
	deadline := time.Now().Add(3 * time.Second)
	for {
		e = windows.LockFileEx(windows.Handle(f.Fd()), windows.LOCKFILE_EXCLUSIVE_LOCK|windows.LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, over)
		if e == nil {
			return func() { _ = windows.UnlockFileEx(windows.Handle(f.Fd()), 0, 1, 0, over); _ = f.Close() }, nil
		}
		if time.Now().After(deadline) {
			f.Close()
			return nil, e
		}
		time.Sleep(10 * time.Millisecond)
	}
}

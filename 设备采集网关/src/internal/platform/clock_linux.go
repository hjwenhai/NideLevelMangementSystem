//go:build linux

package platform

import (
	"golang.org/x/sys/unix"
	"time"
)

func SetClock(t time.Time) error {
	tv := unix.NsecToTimeval(t.UnixNano())
	return unix.Settimeofday(&tv)
}

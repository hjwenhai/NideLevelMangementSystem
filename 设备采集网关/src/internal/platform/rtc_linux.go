//go:build linux

package platform

import (
	"fmt"
	"golang.org/x/sys/unix"
	"levelgateway/src/internal/clockstate"
	"os"
	"time"
)

// The RTC is always UTC, independent of /etc/localtime or the browser timezone.
func ReadRTC(path string) (clockstate.RTCReading, error) {
	f, err := os.Open(path)
	if err != nil {
		return clockstate.RTCReading{}, err
	}
	defer f.Close()
	r, err := unix.IoctlGetRTCTime(int(f.Fd()))
	if err != nil {
		return clockstate.RTCReading{}, err
	}
	t := time.Date(int(r.Year)+1900, time.Month(r.Mon+1), int(r.Mday), int(r.Hour), int(r.Min), int(r.Sec), 0, time.UTC)
	if t.Year() != int(r.Year)+1900 || int(t.Month()) != int(r.Mon)+1 || t.Day() != int(r.Mday) ||
		t.Hour() != int(r.Hour) || t.Minute() != int(r.Min) || t.Second() != int(r.Sec) {
		return clockstate.RTCReading{}, fmt.Errorf("RTC返回无效日历字段")
	}
	flags, flagErr := unix.IoctlGetUint32(int(f.Fd()), unix.RTC_VL_READ)
	if flagErr != nil && flagErr != unix.ENOTTY && flagErr != unix.EINVAL && flagErr != unix.EOPNOTSUPP {
		return clockstate.RTCReading{}, fmt.Errorf("读取RTC有效性失败：%w", flagErr)
	}
	return clockstate.RTCReading{Time: t, ValiditySupported: flagErr == nil, Flags: flags}, nil
}

func WriteRTC(path string, t time.Time) error {
	f, err := os.OpenFile(path, os.O_RDWR, 0)
	if err != nil {
		return err
	}
	defer f.Close()
	t = t.UTC()
	r := unix.RTCTime{Sec: int32(t.Second()), Min: int32(t.Minute()), Hour: int32(t.Hour()), Mday: int32(t.Day()),
		Mon: int32(t.Month()) - 1, Year: int32(t.Year()) - 1900, Wday: int32(t.Weekday()), Yday: int32(t.YearDay()) - 1}
	return unix.IoctlSetRTCTime(int(f.Fd()), &r)
}

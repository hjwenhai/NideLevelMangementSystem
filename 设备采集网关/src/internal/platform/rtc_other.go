//go:build !linux

package platform

import (
	"fmt"
	"levelgateway/src/internal/clockstate"
	"time"
)

func ReadRTC(path string) (clockstate.RTCReading, error) {
	return clockstate.RTCReading{}, fmt.Errorf("RTC仅支持Linux")
}
func WriteRTC(path string, t time.Time) error { return fmt.Errorf("RTC仅支持Linux") }

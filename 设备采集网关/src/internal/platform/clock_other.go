//go:build !linux

package platform

import (
	"fmt"
	"time"
)

func SetClock(t time.Time) error { return fmt.Errorf("实机校时仅支持Linux") }

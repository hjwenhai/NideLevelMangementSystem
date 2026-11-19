// Package clockstate keeps clock provenance separately from measurement freshness.
package clockstate

import (
	"encoding/json"
	"fmt"
	"levelgateway/src/internal/config"
	"os"
	"time"
)

const tolerance = 2 * time.Second
const maxOffline = 366 * 24 * time.Hour

type RTCReading struct {
	Time              time.Time
	ValiditySupported bool
	Flags             uint32
}

// Ops permits tests to exercise cold boots without changing the test host clock.
type Ops struct {
	Now         func() time.Time
	Sleep       func(time.Duration)
	SetSystem   func(time.Time) error
	ReadRTC     func(string) (RTCReading, error)
	WriteRTC    func(string, time.Time) error
	WriteRecord func(string, []byte) error
}

type Record struct {
	Schema       int    `json:"schema"`
	CalibratedMS int64  `json:"calibrated_ms"`
	LastSeenMS   int64  `json:"last_seen_ms"`
	RTCDevice    string `json:"rtc_device"`
	RTCSaved     bool   `json:"rtc_saved"`
	Invalidated  bool   `json:"invalidated"`
	Reason       string `json:"reason,omitempty"`
}

type Status struct {
	Trusted              bool   `json:"trusted"`
	Source               string `json:"source"`
	Reason               string `json:"reason"`
	RTCDevice            string `json:"rtc_device"`
	RTCSaved             bool   `json:"rtc_saved"`
	RTCValiditySupported bool   `json:"rtc_validity_supported"`
	CalibratedMS         int64  `json:"calibrated_ms"`
	CheckedMS            int64  `json:"checked_ms"`
	Warning              string `json:"warning,omitempty"`
}

// Controller is serialized by the caller. Records use atomic replacement and fsync.
type Controller struct {
	Path     string
	Device   string
	Simulate bool
	Ops      Ops
	Record   Record
	Status   Status
}

func New(path, device string, simulate bool, ops Ops) *Controller {
	return &Controller{Path: path, Device: device, Simulate: simulate, Ops: ops,
		Status: Status{Source: "unconfirmed", Reason: "尚未校核时间", RTCDevice: device}}
}

func validTime(t time.Time) bool {
	return t.UnixMilli() >= 1577836800000 && t.UnixMilli() <= 4102444800000
}
func near(a, b time.Time) bool { d := a.Sub(b); return d >= -tolerance && d <= tolerance }
func (c *Controller) save() error {
	if c.Simulate {
		return nil
	}
	b, err := json.MarshalIndent(c.Record, "", "  ")
	if err != nil {
		return err
	}
	write := c.Ops.WriteRecord
	if write == nil {
		write = config.WriteAtomic
	}
	return write(c.Path, append(b, '\n'))
}
func (c *Controller) read() (RTCReading, error) {
	r, err := c.Ops.ReadRTC(c.Device)
	if err != nil {
		return r, err
	}
	if !validTime(r.Time) {
		return r, fmt.Errorf("RTC日期不在2020～2100年范围")
	}
	if r.Flags != 0 {
		return r, fmt.Errorf("RTC电压/数据有效性告警：0x%X", r.Flags)
	}
	return r, nil
}

// Restore never trusts a calendar merely because it looks plausible or a previous
// process trusted it. A durable calibration and a running, non-regressing RTC are required.
func (c *Controller) Restore(allowSet bool) {
	if c.Simulate {
		c.Status.Reason = "模拟模式须在本次运行校核电脑时间"
		return
	}
	fail := func(err error) {
		c.Status.Trusted = false
		c.Status.Source = "unconfirmed"
		c.Status.Reason = err.Error()
	}
	b, err := os.ReadFile(c.Path)
	if err != nil {
		fail(fmt.Errorf("未取得校时记录，请在网页校核时间：%w", err))
		return
	}
	if len(b) > 8192 {
		fail(fmt.Errorf("校时记录过大"))
		return
	}
	if err = json.Unmarshal(b, &c.Record); err != nil || c.Record.Schema != 1 ||
		!validTime(time.UnixMilli(c.Record.CalibratedMS)) || c.Record.LastSeenMS < c.Record.CalibratedMS ||
		!validTime(time.UnixMilli(c.Record.LastSeenMS)) {
		fail(fmt.Errorf("校时记录无效，请重新校核时间"))
		return
	}
	c.Status.CalibratedMS = c.Record.CalibratedMS
	if c.Record.Invalidated || !c.Record.RTCSaved || c.Record.RTCDevice != c.Device {
		fail(fmt.Errorf("上次校时未形成可恢复的RTC记录，或时间已失效/RTC路径已变更，请重新校核"))
		return
	}
	r, err := c.read()
	if err != nil {
		fail(fmt.Errorf("RTC读取失败：%w", err))
		return
	}
	// A second read prevents accepting an oscillator that is stopped at a plausible date.
	c.Ops.Sleep(1100 * time.Millisecond)
	r2, err := c.read()
	if err != nil {
		fail(fmt.Errorf("RTC走时检查失败：%w", err))
		return
	}
	step := r2.Time.Sub(r.Time)
	if step < time.Second || step > 3*time.Second {
		fail(fmt.Errorf("RTC未正常走时，请检查电池/驱动"))
		return
	}
	last := time.UnixMilli(c.Record.LastSeenMS)
	if r2.Time.Before(last.Add(-tolerance)) || r2.Time.After(last.Add(maxOffline)) {
		fail(fmt.Errorf("RTC早于最近可信时间，或离线超过366天，请重新校核"))
		return
	}
	if !near(c.Ops.Now(), r2.Time) {
		if !allowSet {
			fail(fmt.Errorf("系统时间与RTC不一致；启用ALLOW_CLOCK_SET后重启或在网页校核"))
			return
		}
		if err = c.Ops.SetSystem(r2.Time); err != nil {
			fail(fmt.Errorf("RTC恢复系统时间失败：%w", err))
			return
		}
	}
	if !near(c.Ops.Now(), r2.Time) {
		fail(fmt.Errorf("系统时钟回读与RTC不一致"))
		return
	}
	c.Status = Status{Trusted: true, Source: "rtc", Reason: "已根据校时记录及RTC走时检查自动确认", RTCDevice: c.Device,
		RTCSaved: true, RTCValiditySupported: r2.ValiditySupported, CalibratedMS: c.Record.CalibratedMS, CheckedMS: c.Ops.Now().UnixMilli()}
	if !r2.ValiditySupported {
		c.Status.Warning = "RTC驱动不提供电池/失效标志；已核对日期与走时，后备电池仍需断电验收"
	}
	if err = c.Checkpoint(); err != nil {
		fail(fmt.Errorf("保存开机时间检查结果失败：%w", err))
	}
}

// Calibrate confirms this session even if RTC is missing, but explicitly reports
// that cold boot recovery is unavailable. A tombstone precedes any clock writes.
func (c *Controller) Calibrate(utcMS int64, allowSet bool) (resultErr error) {
	defer func() {
		if resultErr != nil && !c.Status.Trusted {
			c.Status.Source = "unconfirmed"
			c.Status.Reason = resultErr.Error()
		}
	}()
	started := c.Ops.Now()
	target := time.UnixMilli(utcMS)
	if !validTime(target) {
		return fmt.Errorf("校时日期须2020～2100年")
	}
	if !near(c.Ops.Now(), target) {
		if c.Simulate {
			return fmt.Errorf("模拟模式不修改电脑系统时钟，请同步电脑时间后重试")
		}
		if !allowSet {
			return fmt.Errorf("实机校时尚未启用；设置ALLOW_CLOCK_SET=1并重启")
		}
	}
	c.Record = Record{Schema: 1, CalibratedMS: utcMS, LastSeenMS: utcMS, RTCDevice: c.Device, Invalidated: true, Reason: "校时进行中"}
	c.Status = Status{Source: "unconfirmed", Reason: "校时进行中", RTCDevice: c.Device}
	if err := c.save(); err != nil {
		return fmt.Errorf("保存校时记录失败，未修改时钟：%w", err)
	}
	// File synchronization can take seconds on eMMC. Advance the supplied time
	// by local monotonic elapsed time rather than setting the clock to an old request.
	target = target.Add(c.Ops.Now().Sub(started))
	if !near(c.Ops.Now(), target) {
		if !allowSet || c.Simulate {
			return fmt.Errorf("校时期间系统时间发生变化，未获准修改，请重试")
		}
		if err := c.Ops.SetSystem(target); err != nil {
			return fmt.Errorf("修改系统时间失败：%w", err)
		}
	}
	if !near(c.Ops.Now(), target) {
		return fmt.Errorf("系统时间回读不一致")
	}
	c.Record.LastSeenMS = c.Ops.Now().UnixMilli()
	if c.Record.LastSeenMS < c.Record.CalibratedMS {
		c.Record.LastSeenMS = c.Record.CalibratedMS
	}
	c.Record.Invalidated = false
	c.Record.Reason = ""
	c.Status = Status{Trusted: true, Source: "browser", Reason: "本次运行已通过电脑/平板校核", RTCDevice: c.Device,
		CalibratedMS: utcMS, CheckedMS: c.Ops.Now().UnixMilli()}
	if c.Simulate {
		c.Status.Warning = "模拟模式不访问RTC，不恢复跨进程信任"
		return nil
	}
	var rtcErr error
	if !allowSet {
		rtcErr = fmt.Errorf("未启用ALLOW_CLOCK_SET，未写入RTC")
	} else {
		rtcErr = c.Ops.WriteRTC(c.Device, c.Ops.Now().UTC())
		if rtcErr == nil {
			var r RTCReading
			r, rtcErr = c.read()
			if rtcErr == nil && !near(r.Time, c.Ops.Now()) {
				rtcErr = fmt.Errorf("RTC写入后回读不一致")
			}
			if rtcErr == nil {
				c.Record.RTCSaved = true
				c.Status.RTCSaved = true
				c.Status.RTCValiditySupported = r.ValiditySupported
				c.Status.Reason = "电脑/平板校时成功，已写入RTC并回读确认"
				if !r.ValiditySupported {
					c.Status.Warning = "RTC驱动不提供电池/失效标志；后备电池仍需断电验收"
				}
			}
		}
	}
	if rtcErr != nil {
		c.Status.Warning = "本次时间已确认，但RTC未保存，断电后需重新校核：" + rtcErr.Error()
	}
	if err := c.save(); err != nil {
		c.Status.Trusted = false
		return fmt.Errorf("保存校时结果失败：%w", err)
	}
	return nil
}

func (c *Controller) Checkpoint() error {
	if !c.Status.Trusted || c.Simulate {
		return nil
	}
	c.Record.LastSeenMS = c.Ops.Now().UnixMilli()
	if c.Record.LastSeenMS < c.Record.CalibratedMS {
		c.Record.LastSeenMS = c.Record.CalibratedMS
	}
	return c.save()
}

func (c *Controller) Invalidate(reason string) error {
	c.Status.Trusted = false
	c.Status.Source = "unconfirmed"
	c.Status.Reason = reason
	c.Record.Invalidated = true
	c.Record.Reason = reason
	return c.save()
}

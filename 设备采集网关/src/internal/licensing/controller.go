package licensing

import (
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"os"
)

type Status struct {
	Required        bool   `json:"required"`
	Valid           bool   `json:"valid"`
	FileValid       bool   `json:"file_valid"`
	Permanent       bool   `json:"permanent"`
	RestartRequired bool   `json:"restart_required"`
	State           string `json:"state"`
	DeviceCode      string `json:"device_code"`
	Kind            string `json:"identity_kind"`
	ID              string `json:"license_id"`
	Customer        string `json:"customer"`
	Message         string `json:"message"`
}
type Controller struct {
	Path      string
	Required  bool
	Reader    Reader
	keys      PublicKeys
	active    bool
	grant     Grant
	loadError error
}

func New(path string, required bool, reader Reader) *Controller {
	return NewWithKeys(path, required, reader, TrustedKeys())
}
func NewWithKeys(path string, required bool, reader Reader, keys PublicKeys) *Controller {
	if reader == nil {
		reader = HardwareReader
	}
	c := &Controller{Path: path, Required: required, Reader: reader, keys: keys}
	if !required {
		c.active = true
		return c
	}
	c.grant, c.loadError = c.read()
	c.active = c.loadError == nil
	return c
}
func (c *Controller) Allowed() bool { return c == nil || c.active }
func (c *Controller) verify(b []byte) (Grant, error) {
	g, e := VerifySignature(b, c.keys)
	if e != nil {
		return Grant{}, e
	}
	v, e := c.Reader(g.Kind)
	if e != nil {
		return Grant{}, fmt.Errorf("无法读取本授权对应的硬件编号")
	}
	code, e := Fingerprint(g.Kind, v)
	if e != nil || code != g.DeviceCode {
		return Grant{}, fmt.Errorf("授权与本台网关不匹配，不能用于其他设备")
	}
	return g, nil
}
func (c *Controller) read() (Grant, error) {
	f, e := os.Open(c.Path)
	if e != nil {
		if os.IsNotExist(e) {
			return Grant{}, fmt.Errorf("尚未安装永久授权，请向厂家提供设备申请文件")
		}
		return Grant{}, fmt.Errorf("无法读取授权文件")
	}
	defer f.Close()
	b, e := io.ReadAll(io.LimitReader(f, Limit+1))
	if e != nil {
		return Grant{}, e
	}
	return c.verify(b)
}
func (c *Controller) Request() (Request, error) {
	if c == nil {
		return Request{}, fmt.Errorf("当前未启用设备授权")
	}
	if g, e := c.read(); e == nil {
		return Request{1, Product, g.Kind, g.DeviceCode}, nil
	}
	return Probe(c.Reader)
}
func (c *Controller) Status() Status {
	if c == nil || !c.Required {
		return Status{Valid: true, State: "simulation", Message: "模拟运行，不访问真实串口；不作为设备永久授权"}
	}
	s := Status{Required: true, Valid: c.active, State: "locked", Message: "未授权：仅开放管理网页，不启动液位计采集、控制或北向服务"}
	req, e := c.Request()
	if e == nil {
		s.DeviceCode = req.DeviceCode
		s.Kind = req.Kind
	} else {
		s.State = "hardware_unavailable"
		s.Message = e.Error()
	}
	g, err := c.read()
	s.FileValid = err == nil
	if c.active {
		s.State = "active"
		s.Permanent = true
		s.ID = c.grant.ID
		s.Customer = c.grant.Customer
		s.Kind = c.grant.Kind
		s.DeviceCode = c.grant.DeviceCode
		s.Message = "本机永久授权已生效，无到期时间，不需要联网续期"
		if err != nil {
			s.Message = "本次运行授权已通过，但保存的授权文件异常；重启前请重新导入厂家原文件"
		} else if g.ID != c.grant.ID {
			s.RestartRequired = true
			s.Message = "本次运行授权继续有效；新授权已保存，人工重启网关程序后更新客户及授权记录"
		}
	} else if err == nil {
		s.State = "restart_required"
		s.RestartRequired = true
		s.Permanent = true
		s.ID = g.ID
		s.Customer = g.Customer
		s.Message = "本机永久授权已保存，重启网关程序后开始采集"
	} else if e == nil {
		s.Message = err.Error() + "；当前仅开放管理网页，不采集或控制仪表"
	}
	return s
}
func (c *Controller) Import(b []byte) error {
	if c == nil || !c.Required {
		return fmt.Errorf("当前模拟模式不安装真实设备授权")
	}
	if _, e := c.verify(b); e != nil {
		return e
	}
	return config.WriteAtomic(c.Path, b)
}
func (c *Controller) CheckFile() error { _, e := c.read(); return e }

// SavedGrant reads and verifies the grant currently on disk, including a newly
// imported grant that the acquisition service will first use on its next boot.
func (c *Controller) SavedGrant() (Grant, error) { return c.read() }
func (c *Controller) RequestJSON() ([]byte, error) {
	r, e := c.Request()
	if e != nil {
		return nil, e
	}
	return json.MarshalIndent(r, "", "  ")
}

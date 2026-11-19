package config

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"strconv"
)

const (
	MaxMeters     = 64
	MaxPerPort    = 16
	SchemaVersion = 3
	South         = "south_rtu_master"
	North         = "north_rtu_slave"
	Disabled      = "disabled"
)

// CommitError means rename succeeded but directory durability is uncertain.
// Callers must not pretend the old on-disk configuration is still active.
type CommitError struct{ Err error }

func (e *CommitError) Error() string {
	return "文件已原子替换，但目录同步失败: " + e.Err.Error()
}
func (e *CommitError) Unwrap() error { return e.Err }

var syncDir = syncDirectory

type Port struct {
	ID          int    `json:"id"`
	Role        string `json:"role"`
	Baud        int    `json:"baud"`
	Parity      string `json:"parity"`
	StopBits    int    `json:"stop_bits"`
	TimeoutMS   int    `json:"timeout_ms"`
	UnitID      uint8  `json:"unit_id"`
	AllowWrites bool   `json:"allow_writes"`
}
type Meter struct {
	Slot        int    `json:"slot"`
	Port        int    `json:"port"`
	Address     uint8  `json:"address"`
	Enabled     bool   `json:"enabled"`
	Name        string `json:"name"`
	Binding     uint32 `json:"binding_version"`
	Temperature bool   `json:"temperature_detail"`
	Density     bool   `json:"density_detail"`
	Analog      bool   `json:"analog"`
}
type TCP struct {
	Enabled     bool   `json:"enabled"`
	Listen      string `json:"listen"`
	UnitID      uint8  `json:"unit_id"`
	MaxClients  int    `json:"max_clients"`
	AllowWrites bool   `json:"allow_writes"`
}
type History struct {
	PeriodSec     int `json:"period_sec"`
	RetentionDays int `json:"retention_days"`
	MaxMB         int `json:"max_mb"`
}
type Config struct {
	Schema       int      `json:"schema_version"`
	Revision     uint32   `json:"revision"`
	Mode         string   `json:"mode"`
	Ports        []Port   `json:"ports"`
	Meters       []Meter  `json:"meters"`
	TCP          TCP      `json:"tcp"`
	FastMS       int      `json:"fast_ms"`
	DetailMS     int      `json:"detail_ms"`
	History      History  `json:"history"`
	Actions      []uint16 `json:"allowed_actions"`
	SlotVersions []uint32 `json:"slot_versions"`
}

func Parse(b []byte) (Config, error) {
	var c Config
	d := json.NewDecoder(bytes.NewReader(b))
	d.DisallowUnknownFields()
	if err := d.Decode(&c); err != nil {
		return c, err
	}
	var extra any
	if err := d.Decode(&extra); err != io.EOF {
		return c, fmt.Errorf("配置必须只有一个JSON对象")
	}
	// Only the known 32-slot schema2 can be upgraded. Never reinterpret legacy schema1.
	if c.Schema == 2 {
		if len(c.SlotVersions) != 32 {
			return c, fmt.Errorf("schema2要求原32槽位配置")
		}
		for _, m := range c.Meters {
			if m.Slot < 1 || m.Slot > 32 {
				return c, fmt.Errorf("schema2仪表编号越界")
			}
		}
		c.Schema = SchemaVersion
		for len(c.SlotVersions) < MaxMeters {
			c.SlotVersions = append(c.SlotVersions, 1)
		}
	}
	return c, c.Validate()
}
func Load(path string) (Config, error) {
	b, e := os.ReadFile(path)
	if e != nil {
		return Config{}, e
	}
	return Parse(b)
}
func (c Config) Clone() Config {
	b, _ := json.Marshal(c)
	var v Config
	_ = json.Unmarshal(b, &v)
	return v
}
func (c Config) Port(id int) Port {
	for _, p := range c.Ports {
		if p.ID == id {
			return p
		}
	}
	return Port{}
}
func (c Config) Validate() error {
	if c.Schema != SchemaVersion || c.Revision == 0 {
		return fmt.Errorf("配置版本无效：要求schema_version=3、64槽位，每口最多16台")
	}
	if len(c.SlotVersions) != MaxMeters {
		return fmt.Errorf("必须有64个槽位版本")
	}
	for _, v := range c.SlotVersions {
		if v == 0 {
			return fmt.Errorf("槽位版本不能为0")
		}
	}
	if c.Mode != "normal" && c.Mode != "maintenance" {
		return fmt.Errorf("mode必须normal或maintenance")
	}
	if len(c.Ports) != 4 {
		return fmt.Errorf("必须配置4个物理口")
	}
	seen := map[int]bool{}
	south, north := 0, 0
	for _, p := range c.Ports {
		if p.ID < 1 || p.ID > 4 || seen[p.ID] {
			return fmt.Errorf("COM编号必须1～4且唯一")
		}
		seen[p.ID] = true
		if p.Role != South && p.Role != North && p.Role != Disabled {
			return fmt.Errorf("COM%d角色无效", p.ID)
		}
		if p.Role == South {
			south++
			if p.Baud != 1200 && p.Baud != 2400 && p.Baud != 4800 && p.Baud != 9600 {
				return fmt.Errorf("南向波特率不受支持")
			}
			if p.Parity != "E" || p.StopBits != 1 {
				return fmt.Errorf("FW-9000南向必须8E1")
			}
		}
		if p.Role == North {
			north++
			if p.UnitID < 1 || p.UnitID > 247 {
				return fmt.Errorf("北向站号无效")
			}
		}
		validBaud := false
		for _, v := range []int{1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200} {
			if p.Baud == v {
				validBaud = true
			}
		}
		if !validBaud || (p.Parity != "E" && p.Parity != "O" && p.Parity != "N") || (p.StopBits != 1 && p.StopBits != 2) || p.TimeoutMS < 50 || p.TimeoutMS > 5000 {
			return fmt.Errorf("COM%d串口参数无效", p.ID)
		}
	}
	if c.Mode == "normal" && south < 2 {
		return fmt.Errorf("正常模式至少2路南向")
	}
	if c.Mode == "normal" && north == 0 && !c.TCP.Enabled {
		return fmt.Errorf("没有北向访问方式")
	}
	if c.TCP.MaxClients < 1 || c.TCP.MaxClients > 16 || c.TCP.UnitID < 1 || c.TCP.UnitID > 247 {
		return fmt.Errorf("TCP参数无效")
	}
	host, port, err := net.SplitHostPort(c.TCP.Listen)
	if err != nil {
		return fmt.Errorf("TCP监听地址无效")
	}
	if host != "" && net.ParseIP(host) == nil {
		return fmt.Errorf("监听地址须为IP")
	}
	portNumber, err := strconv.Atoi(port)
	if err != nil || portNumber < 1 || portNumber > 65535 {
		return fmt.Errorf("TCP端口须1～65535")
	}
	if c.FastMS < 1000 || c.FastMS > 60000 || c.DetailMS < c.FastMS || c.DetailMS > 3600000 {
		return fmt.Errorf("采集周期无效")
	}
	if c.History.PeriodSec < 1 || c.History.PeriodSec > 86400 || c.History.RetentionDays < 1 || c.History.RetentionDays > 365 || c.History.MaxMB < 16 || c.History.MaxMB > 2048 {
		return fmt.Errorf("历史策略无效")
	}
	slots := map[int]bool{}
	addresses := map[string]bool{}
	counts := map[int]int{}
	for _, m := range c.Meters {
		if m.Slot < 1 || m.Slot > MaxMeters || slots[m.Slot] || m.Port < 1 || m.Port > 4 || m.Address < 1 || m.Address > 243 || m.Binding == 0 || len(m.Name) > 128 {
			return fmt.Errorf("槽位、地址、绑定版本或名称无效")
		}
		if m.Binding != c.SlotVersions[m.Slot-1] {
			return fmt.Errorf("绑定版本不匹配")
		}
		slots[m.Slot] = true
		if m.Enabled {
			if c.Port(m.Port).Role != South {
				return fmt.Errorf("槽位%d不在南向口", m.Slot)
			}
			key := fmt.Sprintf("%d/%d", m.Port, m.Address)
			if addresses[key] {
				return fmt.Errorf("COM%d有重复地址", m.Port)
			}
			addresses[key] = true
			counts[m.Port]++
			if counts[m.Port] > MaxPerPort {
				return fmt.Errorf("COM%d超过16台", m.Port)
			}
		}
	}
	for _, a := range c.Actions {
		if a != 1 && a != 2 && a != 4 && a != 8 && a != 16 && a != 32 && a != 64 && a != 128 && a != 256 && a != 512 {
			return fmt.Errorf("动作必须是已定义的单个位")
		}
	}
	return nil
}
func Default(n int) Config {
	if n < 0 || n > MaxMeters {
		panic("default meter count must be 0..64")
	}
	c := Config{Schema: SchemaVersion, Revision: 1, Mode: "normal", TCP: TCP{true, "127.0.0.1:1502", 1, 8, false}, FastMS: 3000, DetailMS: 30000, History: History{60, 30, 256}, Actions: []uint16{}}
	c.SlotVersions = make([]uint32, MaxMeters)
	for i := range c.SlotVersions {
		c.SlotVersions[i] = 1
	}
	for i := 1; i <= 4; i++ {
		role := Disabled
		baud := 4800
		if i < 3 {
			role = South
		}
		if i == 3 {
			role = North
			baud = 38400
		}
		c.Ports = append(c.Ports, Port{i, role, baud, "E", 1, 500, 1, false})
	}
	for i := 1; i <= n; i++ {
		p := (i-1)/MaxPerPort + 1
		if p > 2 {
			c.Ports[p-1].Role = South
			c.Ports[p-1].Baud = 4800
		}
		c.Meters = append(c.Meters, Meter{i, p, uint8((i-1)%MaxPerPort + 1), true, fmt.Sprintf("液位计%02d", i), 1, true, true, false})
	}
	if n > 32 && n <= 48 {
		c.Ports[3].Role = North
		c.Ports[3].Baud = 38400
	}
	return c
}

// The active configuration is only replaced after the candidate ports have opened.
func SaveAtomic(path string, c Config) error {
	if err := c.Validate(); err != nil {
		return err
	}
	b, e := json.MarshalIndent(c, "", "  ")
	if e != nil {
		return e
	}
	return WriteAtomic(path, append(b, '\n'))
}
func WriteAtomic(path string, b []byte) error {
	if e := os.MkdirAll(filepath.Dir(path), 0700); e != nil {
		return e
	}
	f, e := os.CreateTemp(filepath.Dir(path), ".gateway-*")
	if e != nil {
		return e
	}
	tmp := f.Name()
	defer os.Remove(tmp)
	if e = f.Chmod(0600); e == nil {
		_, e = f.Write(b)
	}
	if e == nil {
		e = f.Sync()
	}
	closeErr := f.Close()
	if e == nil {
		e = closeErr
	}
	if e != nil {
		return e
	}
	if e = os.Rename(tmp, path); e != nil {
		return e
	}
	if e = syncDir(filepath.Dir(path)); e != nil {
		return &CommitError{Err: e}
	}
	return nil
}

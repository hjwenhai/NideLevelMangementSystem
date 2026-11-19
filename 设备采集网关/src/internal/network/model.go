// Package network implements a recoverable, static IPv4 transaction for the
// manufacturer's Buildroot network files. It never changes serial bindings.
package network

import (
	"encoding/binary"
	"fmt"
	"net"
	"regexp"
	"strings"
)

const TrialSeconds = 180

type Interface struct {
	Name    string `json:"name"`
	IP      string `json:"ip"`
	Mask    string `json:"mask"`
	Gateway string `json:"gateway"`
}
type Settings struct {
	Interfaces   []Interface `json:"interfaces"`
	TCPInterface string      `json:"tcp_interface"`
}
type Status struct {
	Supported    bool        `json:"supported"`
	ID           string      `json:"id"`
	Phase        string      `json:"phase"`
	Busy         bool        `json:"busy"`
	Message      string      `json:"message"`
	DeadlineMS   int64       `json:"deadline_ms"`
	Settings     Settings    `json:"settings"`
	Actual       []Interface `json:"actual"`
	WebPort      string      `json:"web_port"`
	FUNAvailable bool        `json:"fun_available"`
	Warning      string      `json:"warning"`
}

func (c Settings) Validate() error {
	if len(c.Interfaces) != 2 || c.Interfaces[0].Name != "eth0" || c.Interfaces[1].Name != "eth1" {
		return fmt.Errorf("必须依次填写eth0、eth1两个网口")
	}
	if c.TCPInterface != "eth0" && c.TCPInterface != "eth1" && c.TCPInterface != "both" {
		return fmt.Errorf("请选择Modbus TCP使用的网口")
	}
	var nets []*net.IPNet
	gateways := 0
	for _, p := range c.Interfaces {
		ip := net.ParseIP(p.IP).To4()
		maskIP := net.ParseIP(p.Mask).To4()
		if ip == nil || maskIP == nil || ip[0] == 0 || ip[0] == 127 || ip[0] >= 224 || ip.IsLinkLocalUnicast() {
			return fmt.Errorf("%s的IPv4地址或掩码无效", p.Name)
		}
		mask := net.IPMask(maskIP)
		ones, bits := mask.Size()
		if bits != 32 || ones < 1 || ones > 30 {
			return fmt.Errorf("%s子网掩码须连续，支持/1～/30", p.Name)
		}
		n := &net.IPNet{IP: ip.Mask(mask), Mask: mask}
		nets = append(nets, n)
		host := binary.BigEndian.Uint32(ip)
		network := binary.BigEndian.Uint32(n.IP)
		broadcast := network | ^binary.BigEndian.Uint32(maskIP)
		if host == network || host == broadcast {
			return fmt.Errorf("%s不能使用子网地址或广播地址", p.Name)
		}
		if p.Gateway != "" {
			gateways++
			g := net.ParseIP(p.Gateway).To4()
			if g == nil || !n.Contains(g) || g.Equal(ip) || binary.BigEndian.Uint32(g) == network || binary.BigEndian.Uint32(g) == broadcast {
				return fmt.Errorf("%s默认网关须为同子网内的其他有效主机地址", p.Name)
			}
		}
	}
	if nets[0].Contains(nets[1].IP) || nets[1].Contains(nets[0].IP) {
		return fmt.Errorf("两个网口的子网不能相同或重叠")
	}
	if gateways > 1 {
		return fmt.Errorf("默认网关只允许在一个网口填写，另一个留空")
	}
	for _, p := range c.Interfaces {
		for _, other := range c.Interfaces {
			if p.Gateway != "" && p.Gateway == other.IP {
				return fmt.Errorf("默认网关不能是本设备地址")
			}
		}
	}
	return nil
}

var assignment = regexp.MustCompile(`^\s*(?:export\s+)?(METHOD|IPADDR|NETMASK|GATEWAY)\s*=`)

func ParseVendor(name string, b []byte) (Interface, error) {
	p := Interface{Name: name}
	method := ""
	for _, line := range strings.Split(string(b), "\n") {
		match := assignment.FindStringSubmatch(line)
		if match == nil {
			continue
		}
		v := strings.TrimSpace(strings.SplitN(line, "=", 2)[1])
		v = strings.TrimSpace(strings.SplitN(v, "#", 2)[0])
		v = strings.Trim(v, `"'`)
		switch match[1] {
		case "METHOD":
			method = v
		case "IPADDR":
			p.IP = v
		case "NETMASK":
			p.Mask = v
		case "GATEWAY":
			if v != "0.0.0.0" {
				p.Gateway = v
			}
		}
	}
	if method != "STATIC" {
		return p, fmt.Errorf("%s当前不是STATIC固定地址，请厂家先停用DHCP并核对系统网络服务", name)
	}
	return p, nil
}
func VendorFile(old []byte, p Interface) []byte {
	lines := []string{}
	for _, line := range strings.Split(strings.TrimRight(string(old), "\r\n"), "\n") {
		if !assignment.MatchString(line) {
			lines = append(lines, line)
		}
	}

	lines = append(lines, "METHOD=STATIC", "IPADDR="+p.IP, "NETMASK="+p.Mask)
	// Match the manufacturer's disabled GATEWAY convention. An active
	// GATEWAY=0.0.0.0 may create a direct default route in vendor boot scripts.
	if p.Gateway == "" {
		lines = append(lines, "#GATEWAY=")
	} else {
		lines = append(lines, "GATEWAY="+p.Gateway)
	}
	return []byte(strings.Join(lines, "\n") + "\n")
}
func listenFor(c Settings, port string) string {
	host := "0.0.0.0"
	for _, p := range c.Interfaces {
		if p.Name == c.TCPInterface {
			host = p.IP
		}
	}
	return net.JoinHostPort(host, port)
}
func Wildcard(listen string) (string, error) {
	_, port, e := net.SplitHostPort(listen)
	if e != nil {
		return "", e
	}
	return net.JoinHostPort("0.0.0.0", port), nil
}

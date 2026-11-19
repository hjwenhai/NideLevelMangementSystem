package network

import (
	"encoding/binary"
	"fmt"
	"net"
	"os"
	"os/exec"
	"strconv"
	"strings"
)

func (s *Service) preflight() error {
	if s.Opts.Fixture {
		return nil
	}
	if _, e := exec.LookPath("ip"); e != nil {
		return fmt.Errorf("系统缺少ip命令，请厂家核查BusyBox网络工具")
	}
	for _, name := range []string{"eth0", "eth1"} {
		i, e := net.InterfaceByName(name)
		if e != nil {
			return e
		}
		a, e := i.Addrs()
		if e != nil {
			return e
		}
		count := 0
		for _, v := range a {
			ip, _, e := net.ParseCIDR(v.String())
			if e == nil && ip.To4() != nil {
				count++
			}
		}
		if count != 1 {
			return fmt.Errorf("%s须只有一个IPv4地址，请厂家先核查别名、DHCP与路由", name)
		}
	}
	// Other network managers must not compete with static settings during a trial.
	procs, _ := os.ReadDir("/proc")
	for _, p := range procs {
		if _, e := strconv.Atoi(p.Name()); e != nil {
			continue
		}
		b, _ := os.ReadFile("/proc/" + p.Name() + "/cmdline")
		text := strings.ReplaceAll(string(b), "\x00", " ")
		if strings.Contains(text, "udhcpc") && (strings.Contains(text, "eth0") || strings.Contains(text, "eth1")) {
			return fmt.Errorf("检测到有线DHCP客户端，请厂家先停用后再使用静态网络设置")
		}
	}
	routes, e := os.ReadFile("/proc/net/route")
	if e != nil {
		return e
	}
	defaults := 0
	actual, _ := s.actual()
	for _, line := range strings.Split(string(routes), "\n") {
		f := strings.Fields(line)
		if len(f) < 8 || (f[0] != "eth0" && f[0] != "eth1") {
			continue
		}
		dest, de := strconv.ParseUint(f[1], 16, 32)
		gw, ge := strconv.ParseUint(f[2], 16, 32)
		if de != nil || ge != nil {
			return fmt.Errorf("系统路由格式无效")
		}
		if dest == 0 {
			defaults++
			metric, err := strconv.Atoi(f[6])
			if err != nil || metric != 0 || defaults > 1 {
				return fmt.Errorf("有线默认路由重复或使用特殊优先级，请厂家先核查")
			}
			continue
		}
		if gw != 0 {
			return fmt.Errorf("%s存在自定义静态路由，网页暂不接管，请厂家处理", f[0])
		}
		mask, me := strconv.ParseUint(f[7], 16, 32)
		if me != nil {
			return me
		}
		bytes := make([]byte, 4)
		binary.LittleEndian.PutUint32(bytes, uint32(mask))
		if n, bits := net.IPMask(bytes).Size(); bits != 32 || n == 0 {
			return fmt.Errorf("%s存在特殊路由，请厂家核查", f[0])
		}
		known := false
		for _, a := range actual {
			if a.Name != f[0] {
				continue
			}
			ip := net.ParseIP(a.IP).To4()
			maskIP := net.ParseIP(a.Mask).To4()
			if ip != nil && maskIP != nil && binary.LittleEndian.Uint32(maskIP) == uint32(mask) && binary.LittleEndian.Uint32(ip.Mask(net.IPMask(maskIP))) == uint32(dest) {
				known = true
			}
		}
		if !known {
			return fmt.Errorf("%s存在额外直连路由，请厂家先核查", f[0])
		}
	}
	return nil
}

package network

import (
	"context"
	"fmt"
	"net"
	"os/exec"
	"runtime"
	"time"
)

func (s *Service) actual() ([]Interface, error) {
	if s.Opts.Fixture {
		c, e := s.settings()
		return c.Interfaces, e
	}
	out := []Interface{}
	for _, name := range []string{"eth0", "eth1"} {
		iface, e := net.InterfaceByName(name)
		if e != nil {
			return nil, e
		}
		addrs, e := iface.Addrs()
		if e != nil {
			return nil, e
		}
		for _, a := range addrs {
			ip, n, e := net.ParseCIDR(a.String())
			if e == nil && ip.To4() != nil {
				out = append(out, Interface{Name: name, IP: ip.String(), Mask: net.IP(n.Mask).String()})
			}
		}
	}
	return out, nil
}
func (s *Service) applyOS(c Settings) error {
	if e := c.Validate(); e != nil {
		return e
	}
	if s.Opts.Fixture {
		return nil
	}
	if runtime.GOOS != "linux" {
		return fmt.Errorf("不支持当前系统网络写入")
	}
	ip, e := exec.LookPath("ip")
	if e != nil {
		return e
	}
	run := func(args ...string) error {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		b, e := exec.CommandContext(ctx, ip, args...).CombinedOutput()
		if e != nil {
			return fmt.Errorf("ip %v: %s: %w", args, b, e)
		}
		return nil
	}
	// Only these two explicitly managed static interfaces are touched; IPv6 and
	// routes on unrelated interfaces are outside this operation.
	for _, p := range c.Interfaces {
		if _, e = net.InterfaceByName(p.Name); e != nil {
			return e
		}
		if e = run("link", "set", "dev", p.Name, "up"); e != nil {
			return e
		}
		// Flush connected/default routes on this managed interface, then its global
		// IPv4 addresses; no shell commands or user-supplied device names are used.
		// Remove only default routes on this interface. Connected routes disappear
		// with their addresses; preflight rejects custom routes on managed ports.
		_ = run("-4", "route", "del", "default", "dev", p.Name)
		if e = run("-4", "addr", "flush", "dev", p.Name, "scope", "global"); e != nil {
			return e
		}
		mask := net.IPMask(net.ParseIP(p.Mask).To4())
		bits, _ := mask.Size()
		if e = run("-4", "addr", "add", fmt.Sprintf("%s/%d", p.IP, bits), "dev", p.Name); e != nil {
			return e
		}
	}
	for _, p := range c.Interfaces {
		if p.Gateway != "" {
			if e = run("-4", "route", "add", "default", "via", p.Gateway, "dev", p.Name); e != nil {
				return e
			}
		}
	}
	actual, e := s.actual()
	if e != nil {
		return e
	}
	for _, p := range c.Interfaces {
		found := false
		for _, a := range actual {
			found = found || (a.Name == p.Name && a.IP == p.IP && a.Mask == p.Mask)
		}
		if !found {
			return fmt.Errorf("%s地址回读不一致", p.Name)
		}
	}
	// Force buffered filesystem metadata to stable storage before reporting trial.
	return syncFilesystem()
}

// Exported only for command-line bootstrap; normal requests use Begin/Confirm.
func (s *Service) ReadState() (State, error) { return s.read() }

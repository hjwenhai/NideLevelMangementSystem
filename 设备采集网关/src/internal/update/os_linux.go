package update

import (
	"fmt"
	"golang.org/x/sys/unix"
	"os"
	"os/exec"
	"strings"
	"syscall"
)

func diskFree(path string) (uint64, error) {
	var s unix.Statfs_t
	e := unix.Statfs(path, &s)
	return s.Bavail * uint64(s.Bsize), e
}
func bootID() string {
	b, e := os.ReadFile("/proc/sys/kernel/random/boot_id")
	if e != nil {
		return "linux-unknown"
	}
	return strings.TrimSpace(string(b))
}
func detach(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true} }
func workerAlive(pid int, id string) bool {
	b, e := os.ReadFile(fmt.Sprintf("/proc/%d/cmdline", pid))
	return e == nil && strings.Contains(string(b), "gateway-updater") && strings.Contains(string(b), id)
}
func replaceLink(from, to, shell string) error { return os.Rename(from, to) }
func nativeExecutable(path string) string      { return path }
func syncDirectory(path string) error {
	f, e := os.Open(path)
	if e != nil {
		return e
	}
	defer f.Close()
	return f.Sync()
}

package update

import (
	"golang.org/x/sys/windows"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
)

func diskFree(path string) (uint64, error) {
	p, e := windows.UTF16PtrFromString(path)
	if e != nil {
		return 0, e
	}
	var n uint64
	e = windows.GetDiskFreeSpaceEx(p, &n, nil, nil)
	return n, e
}
func bootID() string       { return "windows-isolated-fixture" }
func detach(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x200 | 0x08000000} }
func workerAlive(pid int, id string) bool {
	h, e := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, uint32(pid))
	if e != nil {
		return false
	}
	defer windows.CloseHandle(h)
	var code uint32
	return windows.GetExitCodeProcess(h, &code) == nil && code == 259
}
func replaceLink(from, to, shell string) error {
	e := os.Rename(from, to)
	if e == nil {
		return nil
	}
	return exec.Command(filepath.Join(filepath.Dir(shell), "mv.exe"), "-Tf", "--", filepath.ToSlash(from), filepath.ToSlash(to)).Run()
}

// Directory fsync is unavailable in the Windows-only fixture; Linux is durable.
func syncDirectory(string) error { return nil }

// A trailing dot resolves the literal extensionless PE used only in the fixture.
func nativeExecutable(path string) string { return path + "." }

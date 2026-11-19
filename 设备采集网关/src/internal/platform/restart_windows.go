package platform

import (
	"os/exec"
	"syscall"
)

func detachRestart(cmd *exec.Cmd) {
	cmd.SysProcAttr = &syscall.SysProcAttr{CreationFlags: 0x00000200 | 0x08000000}
}

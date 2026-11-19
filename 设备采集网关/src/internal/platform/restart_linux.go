package platform

import (
	"os/exec"
	"syscall"
)

func detachRestart(cmd *exec.Cmd) { cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true} }

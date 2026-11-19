//go:build !linux && !windows

package platform

import "os/exec"

func detachRestart(cmd *exec.Cmd) {}

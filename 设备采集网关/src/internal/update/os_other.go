//go:build !linux && !windows

package update

import (
	"fmt"
	"os"
	"os/exec"
)

func diskFree(string) (uint64, error)          { return 0, fmt.Errorf("不支持的平台") }
func bootID() string                           { return "unsupported" }
func detach(*exec.Cmd)                         {}
func workerAlive(int, string) bool             { return false }
func replaceLink(from, to, shell string) error { return os.Rename(from, to) }
func nativeExecutable(path string) string      { return path }
func syncDirectory(string) error               { return fmt.Errorf("不支持的平台") }

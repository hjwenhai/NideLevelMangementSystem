package platform

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"time"
)

// The worker is a separate session/process, never a child that the supervisors stop.
// Paths come from trusted startup flags, never from an HTTP request.
func StartProgramRestart(script, shell, logPath string) (*exec.Cmd, error) {
	// The production supervisor uses --db data/gateway-v2.db from BASE.
	// Resolve its derived log path in that same working directory.
	var err error
	logPath, err = filepath.Abs(logPath)
	if err != nil {
		return nil, err
	}
	if !filepath.IsAbs(script) || !filepath.IsAbs(shell) || !filepath.IsAbs(logPath) {
		return nil, fmt.Errorf("重启执行路径须为绝对路径")
	}
	if info, err := os.Stat(script); err != nil || !info.Mode().IsRegular() {
		return nil, fmt.Errorf("服务脚本不存在")
	}
	if _, err := os.Stat(shell); err != nil {
		return nil, fmt.Errorf("服务Shell不存在")
	}
	executable, err := os.Executable()
	if err != nil {
		return nil, err
	}
	log, err := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		return nil, err
	}
	defer log.Close()
	cmd := exec.Command(executable, "--program-restart-worker", "--service-script", script, "--service-shell", shell)
	cmd.Stdout = log
	cmd.Stderr = log
	detachRestart(cmd)
	if err = cmd.Start(); err != nil {
		return nil, err
	}
	return cmd, nil
}

func RunProgramRestartWorker(script, shell string) error {
	if !filepath.IsAbs(script) || !filepath.IsAbs(shell) {
		return fmt.Errorf("重启路径无效")
	}
	// Allow the accepted response to reach the browser before stopping its server.
	time.Sleep(2 * time.Second)
	cmd := exec.Command(shell, script, "restart-web")
	cmd.Stdout = os.Stdout
	cmd.Stderr = os.Stderr
	return cmd.Run()
}

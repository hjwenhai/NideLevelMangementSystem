package platform

import (
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
)

func StartNetworkWorker(base, id, script, shell string, fixture bool) (*exec.Cmd, error) {
	if !filepath.IsAbs(base) || !filepath.IsAbs(script) || !filepath.IsAbs(shell) {
		return nil, fmt.Errorf("网络执行器路径须为绝对路径")
	}
	exe, e := os.Executable()
	if e != nil {
		return nil, e
	}
	exe, e = filepath.EvalSymlinks(exe)
	if e != nil {
		return nil, e
	}
	log, e := os.OpenFile(filepath.Join(base, "logs", "network-worker.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0600)
	if e != nil {
		return nil, e
	}
	defer log.Close()
	args := []string{"--network-worker", "--network-base", base, "--network-job", id, "--service-script", script, "--service-shell", shell}
	if fixture {
		args = append(args, "--network-fixture", "--simulate")
	}
	cmd := exec.Command(exe, args...)
	cmd.Stdout = log
	cmd.Stderr = log
	detachRestart(cmd)
	if e = cmd.Start(); e != nil {
		return nil, e
	}
	return cmd, nil
}

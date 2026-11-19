package update

import (
	"fmt"
	"levelgateway/src/internal/config"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

// A dead worker's execution lock can only be reclaimed by the explicit recovery
// command. An absent/invalid owner is not evidence that it is safe to remove it.
func (s *Service) execution(id string, recover bool) (func(), error) {
	job, e := s.job(id)
	if e != nil {
		return nil, e
	}
	lock := filepath.Join(job, "execute.lock")
	if recover {
		serial := filepath.Join(job, "recover.lock")
		if e = os.Mkdir(serial, 0700); e != nil {
			return nil, fmt.Errorf("已有恢复检查，或恢复锁需核查")
		}
		defer os.Remove(serial)
		if _, e = os.Lstat(lock); e == nil {
			b, e := os.ReadFile(filepath.Join(lock, "owner"))
			if e != nil {
				return nil, fmt.Errorf("执行锁归属不明，须厂家核查")
			}
			pid, e := strconv.Atoi(strings.TrimSpace(string(b)))
			if e != nil || pid <= 0 {
				return nil, fmt.Errorf("执行锁归属无效")
			}
			if workerAlive(pid, id) {
				return nil, fmt.Errorf("升级执行器仍在运行")
			}
			if e = os.Remove(filepath.Join(lock, "owner")); e != nil {
				return nil, e
			}
			if e = os.Remove(lock); e != nil {
				return nil, e
			}
		} else if !os.IsNotExist(e) {
			return nil, e
		}
	}
	if e = os.Mkdir(lock, 0700); e != nil {
		return nil, fmt.Errorf("升级执行锁已存在，拒绝重复执行")
	}
	release := func() { _ = os.Remove(filepath.Join(lock, "owner")); _ = os.Remove(lock) }
	if e = config.WriteAtomic(filepath.Join(lock, "owner"), []byte(strconv.Itoa(os.Getpid()))); e != nil {
		release()
		return nil, e
	}
	return release, nil
}

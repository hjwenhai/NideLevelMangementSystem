package update

import (
	"fmt"
	"io/fs"
	"levelgateway/src/internal/buildinfo"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

const bridgeTarget = "0.8.1-20261109-r1"

// A failed pre-license upgrade may have left the signed release installed but
// inactive. The bridge may reuse it only if the whole tree matches the bundle.
func (s *Service) existingTarget(m Manifest) (bool, error) {
	root := s.path("releases", m.Release)
	if _, err := os.Lstat(root); os.IsNotExist(err) {
		return false, nil
	}
	if !buildinfo.LicenseBridge || m.Release != bridgeTarget {
		return false, fmt.Errorf("目标版本目录已存在，不覆盖旧版本")
	}
	expected := make(map[string]File, len(m.Files))
	for _, file := range m.Files {
		expected[file.Name] = file
	}
	err := filepath.WalkDir(root, func(path string, entry fs.DirEntry, err error) error {
		if err != nil {
			return err
		}
		rel, err := filepath.Rel(root, path)
		if err != nil {
			return err
		}
		name := filepath.ToSlash(rel)
		if entry.IsDir() && (name == "." || name == "bin" || name == "deploy") {
			return nil
		}
		f, ok := expected[name]
		info, err := entry.Info()
		if err != nil || !ok || !info.Mode().IsRegular() || info.Size() != f.Size {
			return fmt.Errorf("文件不匹配：%s", name)
		}
		executable := strings.HasPrefix(name, "bin/") || strings.HasSuffix(name, ".sh") || name == "deploy/S99levelgateway"
		if runtime.GOOS != "windows" && executable && info.Mode().Perm()&0100 == 0 {
			return fmt.Errorf("缺少执行权限：%s", name)
		}
		if sum, err := HashFile(path); err != nil || sum != f.SHA256 {
			return fmt.Errorf("摘要不匹配：%s", name)
		}
		delete(expected, name)
		return nil
	})
	if err != nil || len(expected) != 0 {
		return false, fmt.Errorf("已有目标目录与签名升级包不一致，请联系厂家核查")
	}
	return true, nil
}

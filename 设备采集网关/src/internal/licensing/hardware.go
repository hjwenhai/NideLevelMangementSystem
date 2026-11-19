package licensing

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"runtime"
	"strings"
)

type Reader func(string) (string, error)

func normalize(raw string) (string, error) {
	v := strings.TrimPrefix(strings.ToLower(strings.TrimSpace(raw)), "0x")
	if len(v) < 8 || len(v) > 64 || len(v)%2 != 0 || v == strings.Repeat("0", len(v)) || v == strings.Repeat("f", len(v)) {
		return "", fmt.Errorf("硬件编号无效")
	}
	if _, e := hex.DecodeString(v); e != nil {
		return "", fmt.Errorf("硬件编号格式不支持")
	}
	return v, nil
}
func Fingerprint(kind, value string) (string, error) {
	if !kindValid(kind) {
		return "", fmt.Errorf("硬件编号类型无效")
	}
	v, e := normalize(value)
	if e != nil {
		return "", e
	}
	h := sha256.Sum256([]byte(Product + "\n" + kind + "\n" + v))
	return hex.EncodeToString(h[:]), nil
}
func readBounded(path string) ([]byte, error) {
	f, e := os.Open(path)
	if e != nil {
		return nil, e
	}
	defer f.Close()
	b, e := io.ReadAll(io.LimitReader(f, 65537))
	if len(b) > 65536 {
		return nil, fmt.Errorf("硬件信息过大")
	}
	return b, e
}
func HardwareReader(kind string) (string, error) {
	if runtime.GOOS != "linux" {
		return "", fmt.Errorf("当前系统没有可读取的网关硬件编号")
	}
	switch kind {
	case "soc-serial":
		b, e := readBounded("/sys/devices/soc0/serial_number")
		if e != nil {
			return "", e
		}
		return normalize(string(b))
	case "cpu-serial":
		b, e := readBounded("/proc/cpuinfo")
		if e != nil {
			return "", e
		}
		serial := ""
		for _, line := range strings.Split(string(b), "\n") {
			k, v, ok := strings.Cut(line, ":")
			if ok && strings.EqualFold(strings.TrimSpace(k), "serial") {
				n, e := normalize(v)
				if e != nil {
					return "", e
				}
				if serial != "" && serial != n {
					return "", fmt.Errorf("CPU序列号不一致")
				}
				serial = n
			}
		}
		if serial == "" {
			return "", fmt.Errorf("CPU未提供序列号")
		}
		return serial, nil
	case "emmc-cid":
		// Never bind to a removable SD card, MAC/IP, /etc/machine-id or a rootfs file.
		t, e := readBounded("/sys/block/mmcblk0/device/type")
		if e != nil || strings.TrimSpace(string(t)) != "MMC" {
			return "", fmt.Errorf("未确认mmcblk0为板载eMMC")
		}
		b, e := readBounded("/sys/block/mmcblk0/device/cid")
		if e != nil {
			return "", e
		}
		v, e := normalize(string(b))
		if e != nil || len(v) != 32 {
			return "", fmt.Errorf("eMMC CID无效")
		}
		return v, nil
	}
	return "", fmt.Errorf("不支持的硬件编号类型")
}
func Probe(reader Reader) (Request, error) {
	if reader == nil {
		reader = HardwareReader
	}
	for _, k := range []string{"soc-serial", "cpu-serial", "emmc-cid"} {
		v, e := reader(k)
		if e != nil {
			continue
		}
		code, e := Fingerprint(k, v)
		if e == nil {
			return Request{1, Product, k, code}, nil
		}
	}
	return Request{}, fmt.Errorf("未读取到稳定硬件编号，请厂家核查CPU序列号或板载eMMC CID")
}

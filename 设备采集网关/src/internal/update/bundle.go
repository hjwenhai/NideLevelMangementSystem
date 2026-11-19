package update

import (
	"archive/tar"
	"archive/zip"
	"compress/gzip"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strconv"
	"strings"
	"time"
)

const Product = "GT6757X-LevelGateway"
const UploadLimit int64 = 32 << 20
const ExpandLimit int64 = 64 << 20

var RuntimeFiles = []string{"bin/gateway-core", "bin/gateway-web", "bin/gateway-updater", "deploy/install.sh", "deploy/service.sh", "deploy/supervise.sh", "deploy/rollback.sh", "deploy/S99levelgateway", "deploy/settings.example.env", "deploy/update-trust.json", "config.default.json", "RELEASE", "ARCH", "NORTH_VERSION", "UPDATE_VERSION", "SHA256SUMS"}
var releasePattern = regexp.MustCompile(`^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-([0-9]{8})-r([1-9][0-9]*)$`)
var idPattern = regexp.MustCompile(`^[a-f0-9]{32}$`)

type File struct {
	Name   string `json:"name"`
	Size   int64  `json:"size"`
	SHA256 string `json:"sha256"`
}
type Manifest struct {
	Format         int    `json:"format"`
	Product        string `json:"product"`
	Arch           string `json:"arch"`
	Release        string `json:"release"`
	Software       string `json:"software"`
	North          string `json:"north"`
	ConfigSchema   int    `json:"config_schema"`
	DatabaseSchema int    `json:"database_schema"`
	UpdateProtocol int    `json:"update_protocol"`
	KeyID          string `json:"key_id"`
	Notes          string `json:"notes"`
	Created        string `json:"created"`
	PayloadSHA     string `json:"payload_sha256"`
	PayloadSize    int64  `json:"payload_size"`
	UnpackedSize   int64  `json:"unpacked_size"`
	Files          []File `json:"files"`
}
type Trust struct {
	Keys map[string]string `json:"keys"`
}
type Verified struct {
	Manifest       Manifest
	Raw, Signature []byte
}

func strictJSON(b []byte, v any) error {
	d := json.NewDecoder(strings.NewReader(string(b)))
	d.DisallowUnknownFields()
	if e := d.Decode(v); e != nil {
		return e
	}
	if d.Decode(&struct{}{}) != io.EOF {
		return fmt.Errorf("JSON尾部数据无效")
	}
	return nil
}
func releaseParts(s string) ([]int, error) {
	m := releasePattern.FindStringSubmatch(s)
	if m == nil {
		return nil, fmt.Errorf("版本标签无效")
	}
	if _, e := time.Parse("20060102", m[4]); e != nil {
		return nil, e
	}
	p := make([]int, 5)
	for i := range p {
		n, e := strconv.Atoi(m[i+1])
		if e != nil {
			return nil, e
		}
		p[i] = n
	}
	return p, nil
}
func CompareRelease(a, b string) (int, error) {
	x, e := releaseParts(a)
	if e != nil {
		return 0, e
	}
	y, e := releaseParts(b)
	if e != nil {
		return 0, e
	}
	for i := range x {
		if x[i] > y[i] {
			return 1, nil
		}
		if x[i] < y[i] {
			return -1, nil
		}
	}
	return 0, nil
}
func HashFile(p string) (string, error) {
	f, e := os.Open(p)
	if e != nil {
		return "", e
	}
	defer f.Close()
	h := sha256.New()
	_, e = io.Copy(h, f)
	return hex.EncodeToString(h.Sum(nil)), e
}
func loadTrust(p string) (Trust, error) {
	b, e := os.ReadFile(p)
	if e != nil {
		return Trust{}, e
	}
	if len(b) > 65536 {
		return Trust{}, fmt.Errorf("信任文件过大")
	}
	var t Trust
	e = strictJSON(b, &t)
	if e != nil || len(t.Keys) < 1 || len(t.Keys) > 8 {
		return Trust{}, fmt.Errorf("签名公钥配置无效")
	}
	return t, nil
}
func (m Manifest) Validate(arch string) error {
	if m.Format != 1 || m.Product != Product || m.Arch != arch || m.North != "3.2" || m.ConfigSchema != 3 || m.DatabaseSchema != 3 || m.UpdateProtocol != 1 {
		return fmt.Errorf("升级包型号、架构或协议不兼容")
	}
	p, e := releaseParts(m.Release)
	if e != nil {
		return e
	}
	if m.Software != fmt.Sprintf("%d.%d.%d", p[0], p[1], p[2]) {
		return fmt.Errorf("软件版本与标签不一致")
	}
	if len(m.Notes) > 4096 || len(m.KeyID) > 64 || len(m.PayloadSHA) != 64 || m.PayloadSize < 1 || m.PayloadSize > UploadLimit || m.UnpackedSize < 1 || m.UnpackedSize > ExpandLimit {
		return fmt.Errorf("升级清单超出限制")
	}
	expected := map[string]bool{}
	for _, n := range RuntimeFiles {
		expected[n] = true
	}
	var total int64
	if len(m.Files) != len(expected) {
		return fmt.Errorf("运行文件清单数量不符")
	}
	for _, f := range m.Files {
		if !expected[f.Name] || f.Size < 1 || f.Size > ExpandLimit || len(f.SHA256) != 64 {
			return fmt.Errorf("运行文件清单无效: %s", f.Name)
		}
		if _, e := hex.DecodeString(f.SHA256); e != nil {
			return e
		}
		delete(expected, f.Name)
		total += f.Size
	}
	if len(expected) != 0 || total != m.UnpackedSize {
		return fmt.Errorf("清单大小不一致")
	}
	return nil
}
func VerifyBundle(path, trustPath, arch, destination string) (Verified, error) {
	var v Verified
	// Reject oversized/ZIP64 central directories before archive/zip allocates entries.
	f, e := os.Open(path)
	if e != nil {
		return v, e
	}
	info, e := f.Stat()
	if e != nil {
		f.Close()
		return v, e
	}
	if info.Size() < 22 || info.Size() > UploadLimit {
		f.Close()
		return v, fmt.Errorf("升级文件大小无效")
	}
	tail := make([]byte, 22)
	_, e = f.ReadAt(tail, info.Size()-22)
	f.Close()
	if e != nil {
		return v, e
	}
	if string(tail[:4]) != "PK\x05\x06" || binary.LittleEndian.Uint16(tail[4:]) != 0 || binary.LittleEndian.Uint16(tail[6:]) != 0 || binary.LittleEndian.Uint16(tail[8:]) != 3 || binary.LittleEndian.Uint16(tail[10:]) != 3 || binary.LittleEndian.Uint16(tail[20:]) != 0 || binary.LittleEndian.Uint32(tail[12:]) > 4096 || int64(binary.LittleEndian.Uint32(tail[16:]))+int64(binary.LittleEndian.Uint32(tail[12:]))+22 != info.Size() {
		return v, fmt.Errorf("升级文件索引结构无效")
	}
	z, e := zip.OpenReader(path)
	if e != nil {
		return v, fmt.Errorf("请上传厂家签名的.gwup升级文件")
	}
	defer z.Close()
	if len(z.File) != 3 {
		return v, fmt.Errorf("升级文件结构无效")
	}
	files := map[string]*zip.File{}
	for _, f := range z.File {
		if f.Method != zip.Store || files[f.Name] != nil || f.Mode()&os.ModeType != 0 {
			return v, fmt.Errorf("升级文件条目无效")
		}
		files[f.Name] = f
	}
	if files["manifest.json"] == nil || files["signature.ed25519"] == nil || files["runtime.tar.gz"] == nil {
		return v, fmt.Errorf("升级文件缺少签名或内容")
	}
	readSmall := func(f *zip.File, max uint64) ([]byte, error) {
		if f.UncompressedSize64 > max {
			return nil, fmt.Errorf("条目过大")
		}
		r, e := f.Open()
		if e != nil {
			return nil, e
		}
		defer r.Close()
		return io.ReadAll(io.LimitReader(r, int64(max)+1))
	}
	v.Raw, e = readSmall(files["manifest.json"], 32768)
	if e != nil {
		return v, e
	}
	v.Signature, e = readSmall(files["signature.ed25519"], 64)
	if e != nil {
		return v, e
	}
	if e = strictJSON(v.Raw, &v.Manifest); e != nil {
		return v, fmt.Errorf("升级清单无效: %w", e)
	}
	if e = v.Manifest.Validate(arch); e != nil {
		return v, e
	}
	t, e := loadTrust(trustPath)
	if e != nil {
		return v, fmt.Errorf("缺少有效厂家公钥: %w", e)
	}
	key, e := hex.DecodeString(t.Keys[v.Manifest.KeyID])
	if e != nil || len(key) != ed25519.PublicKeySize || !ed25519.Verify(ed25519.PublicKey(key), v.Raw, v.Signature) {
		return v, fmt.Errorf("厂家数字签名校验失败")
	}
	payload := files["runtime.tar.gz"]
	if payload.UncompressedSize64 != uint64(v.Manifest.PayloadSize) {
		return v, fmt.Errorf("升级包长度不符")
	}
	r, e := payload.Open()
	if e != nil {
		return v, e
	}
	h := sha256.New()
	n, e := io.Copy(h, io.LimitReader(r, UploadLimit+1))
	ce := r.Close()
	if e != nil || ce != nil || n != v.Manifest.PayloadSize || hex.EncodeToString(h.Sum(nil)) != v.Manifest.PayloadSHA {
		return v, fmt.Errorf("升级包SHA256不符")
	}
	r, e = payload.Open()
	if e != nil {
		return v, e
	}
	defer r.Close()
	if e = inspectTar(r, v.Manifest, destination); e != nil {
		return v, e
	}
	return v, nil
}
func inspectTar(r io.Reader, m Manifest, dest string) error {
	gz, e := gzip.NewReader(r)
	if e != nil {
		return e
	}
	defer gz.Close()
	limited := &io.LimitedReader{R: gz, N: ExpandLimit + (2 << 20)}
	tr := tar.NewReader(limited)
	root := "gt6757s-" + m.Release + "-" + m.Arch
	expected := map[string]File{}
	for _, f := range m.Files {
		expected[f.Name] = f
	}
	seen := map[string]bool{}
	for {
		h, e := tr.Next()
		if errors.Is(e, io.EOF) {
			break
		}
		if e != nil {
			return e
		}
		name := strings.TrimSuffix(h.Name, "/")
		if h.Typeflag == tar.TypeDir {
			if name != root && name != root+"/bin" && name != root+"/deploy" {
				return fmt.Errorf("非法目录")
			}
			continue
		}
		if h.Typeflag != tar.TypeReg || !strings.HasPrefix(name, root+"/") {
			return fmt.Errorf("升级包包含链接或非法路径")
		}
		rel := strings.TrimPrefix(name, root+"/")
		f, ok := expected[rel]
		if !ok || seen[rel] || h.Size != f.Size {
			return fmt.Errorf("运行文件无效或重复: %s", rel)
		}
		seen[rel] = true
		hash := sha256.New()
		var out *os.File
		var writer io.Writer = hash
		if dest != "" {
			path := filepath.Join(dest, filepath.FromSlash(rel))
			if e = os.MkdirAll(filepath.Dir(path), 0700); e != nil {
				return e
			}
			out, e = os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
			if e != nil {
				return e
			}
			writer = io.MultiWriter(hash, out)
		}
		first := make([]byte, 0, 32)
		buf := make([]byte, 65536)
		var count int64
		for {
			n, re := tr.Read(buf)
			if n > 0 {
				if len(first) < 32 {
					first = append(first, buf[:min(n, 32-len(first))]...)
				}
				wn, we := writer.Write(buf[:n])
				count += int64(wn)
				if we != nil {
					if out != nil {
						out.Close()
					}
					return we
				}
			}
			if re == io.EOF {
				break
			}
			if re != nil {
				if out != nil {
					out.Close()
				}
				return re
			}
		}
		if out != nil {
			e = out.Sync()
			ce := out.Close()
			if e == nil {
				e = ce
			}
			if e != nil {
				return e
			}
			if strings.HasPrefix(rel, "bin/") || strings.HasSuffix(rel, ".sh") || rel == "deploy/S99levelgateway" {
				if e = os.Chmod(filepath.Join(dest, filepath.FromSlash(rel)), 0755); e != nil {
					return e
				}
			}
		}
		if count != f.Size || hex.EncodeToString(hash.Sum(nil)) != f.SHA256 {
			return fmt.Errorf("运行文件摘要不符: %s", rel)
		}
		if strings.HasPrefix(rel, "bin/") {
			if e = checkExecutable(first, m.Arch); e != nil {
				return e
			}
		}
	}
	if len(seen) != len(expected) {
		return fmt.Errorf("运行文件缺失")
	}
	if _, e = io.Copy(io.Discard, limited); e != nil {
		return e
	}
	if limited.N == 0 {
		return fmt.Errorf("解包大小超限")
	}
	return nil
}
func checkExecutable(b []byte, arch string) error {
	if arch == "windows-amd64-test" {
		if len(b) >= 2 && string(b[:2]) == "MZ" {
			return nil
		}
		return fmt.Errorf("测试PE格式无效")
	}
	if len(b) < 20 || string(b[:4]) != "\x7fELF" || b[5] != 1 {
		return fmt.Errorf("运行文件不是小端ELF")
	}
	machine := uint16(b[18]) | uint16(b[19])<<8
	if arch == "linux-arm64" && b[4] == 2 && machine == 183 || arch == "linux-armv7" && b[4] == 1 && machine == 40 {
		return nil
	}
	return fmt.Errorf("运行文件架构不符")
}
func InitSigningKey(privatePath, publicPath string) error {
	if _, e := os.Stat(publicPath); !os.IsNotExist(e) {
		return fmt.Errorf("公钥文件已存在或无法访问，不创建新密钥")
	}
	if e := os.MkdirAll(filepath.Dir(publicPath), 0755); e != nil {
		return e
	}
	if _, e := os.Stat(privatePath); e == nil {
		return fmt.Errorf("签名私钥已存在，拒绝覆盖")
	}
	pub, priv, e := ed25519.GenerateKey(rand.Reader)
	if e != nil {
		return e
	}
	if e = os.MkdirAll(filepath.Dir(privatePath), 0700); e != nil {
		return e
	}
	f, e := os.OpenFile(privatePath, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	if _, e = f.Write(priv); e == nil {
		e = f.Sync()
	}
	f.Close()
	if e != nil {
		return e
	}
	id := sha256.Sum256(pub)
	b, _ := json.MarshalIndent(Trust{Keys: map[string]string{hex.EncodeToString(id[:8]): hex.EncodeToString(pub)}}, "", "  ")
	if _, e = os.Stat(publicPath); e == nil {
		return fmt.Errorf("公钥文件已存在，请核对，不覆盖")
	}
	return os.WriteFile(publicPath, append(b, '\n'), 0644)
}
func SignBundle(payload, stage, out, privatePath, notes string) error {
	private, e := os.ReadFile(privatePath)
	if e != nil || len(private) != ed25519.PrivateKeySize {
		return fmt.Errorf("签名私钥无效")
	}
	priv := ed25519.PrivateKey(private)
	pub := priv.Public().(ed25519.PublicKey)
	id := sha256.Sum256(pub)
	read := func(n string) string {
		b, _ := os.ReadFile(filepath.Join(stage, n))
		return strings.TrimSpace(string(b))
	}
	rel, arch := read("RELEASE"), read("ARCH")
	p, e := releaseParts(rel)
	if e != nil {
		return e
	}
	m := Manifest{Format: 1, Product: Product, Release: rel, Software: fmt.Sprintf("%d.%d.%d", p[0], p[1], p[2]), Arch: arch, North: read("NORTH_VERSION"), ConfigSchema: 3, DatabaseSchema: 3, UpdateProtocol: 1, KeyID: hex.EncodeToString(id[:8]), Notes: notes, Created: time.Now().UTC().Format(time.RFC3339)}
	for _, n := range RuntimeFiles {
		path := filepath.Join(stage, filepath.FromSlash(n))
		info, e := os.Lstat(path)
		if e != nil || !info.Mode().IsRegular() {
			return fmt.Errorf("文件缺失: %s", n)
		}
		s, e := HashFile(path)
		if e != nil {
			return e
		}
		m.Files = append(m.Files, File{n, info.Size(), s})
		m.UnpackedSize += info.Size()
	}
	sort.Slice(m.Files, func(i, j int) bool { return m.Files[i].Name < m.Files[j].Name })
	m.PayloadSHA, e = HashFile(payload)
	if e != nil {
		return e
	}
	info, e := os.Stat(payload)
	if e != nil {
		return e
	}
	m.PayloadSize = info.Size()
	if e = m.Validate(arch); e != nil {
		return e
	}
	raw, _ := json.Marshal(m)
	sig := ed25519.Sign(priv, raw)
	f, e := os.OpenFile(out, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	z := zip.NewWriter(f)
	for _, n := range []string{"manifest.json", "signature.ed25519", "runtime.tar.gz"} {
		w, e := z.CreateHeader(&zip.FileHeader{Name: n, Method: zip.Store})
		if e != nil {
			f.Close()
			return e
		}
		if n == "manifest.json" {
			_, e = w.Write(raw)
		} else if n == "signature.ed25519" {
			_, e = w.Write(sig)
		} else {
			in, er := os.Open(payload)
			if er != nil {
				f.Close()
				return er
			}
			_, e = io.Copy(w, in)
			in.Close()
		}
		if e != nil {
			f.Close()
			return e
		}
	}
	if e = z.Close(); e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e == nil {
		e = ce
	}
	return e
}

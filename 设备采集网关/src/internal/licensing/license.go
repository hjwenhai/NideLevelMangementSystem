// Package licensing provides offline, permanent, hardware-bound licenses.
// Signing keys are never part of a gateway runtime package.
package licensing

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	_ "embed"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"levelgateway/src/internal/config"
	"os"
	"path/filepath"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"
)

const Product = "GT6757X-LevelGateway"
const Limit = 8192

//go:embed trusted-public.json
var embeddedPublic []byte

type PublicKeys struct {
	Keys map[string]string `json:"keys"`
}
type Request struct {
	Format     int    `json:"format"`
	Product    string `json:"product"`
	Kind       string `json:"identity_kind"`
	DeviceCode string `json:"device_code"`
}
type Grant struct {
	Format     int    `json:"format"`
	Product    string `json:"product"`
	ID         string `json:"license_id"`
	Kind       string `json:"identity_kind"`
	DeviceCode string `json:"device_code"`
	Customer   string `json:"customer"`
	Permanent  bool   `json:"permanent"`
	IssuedAt   string `json:"issued_at"`
}
type Envelope struct {
	License   Grant  `json:"license"`
	KeyID     string `json:"key_id"`
	Signature string `json:"signature"`
}

func Decode(b []byte, v any) error {
	if len(b) > Limit {
		return fmt.Errorf("文件过大")
	}
	d := json.NewDecoder(bytes.NewReader(b))
	d.DisallowUnknownFields()
	if e := d.Decode(v); e != nil {
		return e
	}
	if d.Decode(&struct{}{}) != io.EOF {
		return fmt.Errorf("只允许一个JSON对象")
	}
	return nil
}
func text(s string, max int) bool {
	if strings.TrimSpace(s) != s || len(s) == 0 || len(s) > max || !utf8.ValidString(s) {
		return false
	}
	for _, c := range s {
		if unicode.IsControl(c) {
			return false
		}
	}
	return true
}
func codeValid(s string, n int) bool {
	b, e := hex.DecodeString(s)
	return e == nil && len(b) == n && strings.ToLower(s) == s && s != strings.Repeat("0", 2*n)
}
func kindValid(k string) bool { return k == "soc-serial" || k == "cpu-serial" || k == "emmc-cid" }
func (r Request) Validate() error {
	if r.Format != 1 || r.Product != Product || !kindValid(r.Kind) || !codeValid(r.DeviceCode, 32) {
		return fmt.Errorf("设备申请文件无效")
	}
	return nil
}
func (g Grant) Validate() error {
	if (Request{g.Format, g.Product, g.Kind, g.DeviceCode}).Validate() != nil || !codeValid(g.ID, 16) || !text(g.Customer, 128) || !g.Permanent {
		return fmt.Errorf("只接受本产品的永久设备授权")
	}
	if _, e := time.Parse(time.RFC3339, g.IssuedAt); e != nil {
		return fmt.Errorf("签发记录格式无效")
	}
	return nil
}
func signatureData(g Grant) []byte {
	b, _ := json.Marshal(g)
	return append([]byte("GT6757X permanent device license v1\n"), b...)
}
func KeyID(pub ed25519.PublicKey) string { h := sha256.Sum256(pub); return hex.EncodeToString(h[:8]) }
func NewGrant(req Request, customer string, key ed25519.PrivateKey) (Envelope, error) {
	if e := req.Validate(); e != nil {
		return Envelope{}, e
	}
	if len(key) != ed25519.PrivateKeySize {
		return Envelope{}, fmt.Errorf("厂家私钥格式错误")
	}
	id := make([]byte, 16)
	if _, e := rand.Read(id); e != nil {
		return Envelope{}, e
	}
	g := Grant{1, Product, hex.EncodeToString(id), req.Kind, req.DeviceCode, customer, true, time.Now().UTC().Format(time.RFC3339)}
	if e := g.Validate(); e != nil {
		return Envelope{}, e
	}
	pub := key.Public().(ed25519.PublicKey)
	return Envelope{g, KeyID(pub), hex.EncodeToString(ed25519.Sign(key, signatureData(g)))}, nil
}
func VerifySignature(b []byte, keys PublicKeys) (Grant, error) {
	var env Envelope
	if e := Decode(b, &env); e != nil {
		return Grant{}, fmt.Errorf("授权文件格式无效")
	}
	if e := env.License.Validate(); e != nil {
		return Grant{}, e
	}
	raw, ok := keys.Keys[env.KeyID]
	pub, e := hex.DecodeString(raw)
	sig, se := hex.DecodeString(env.Signature)
	if !ok || e != nil || len(pub) != ed25519.PublicKeySize || se != nil || len(sig) != ed25519.SignatureSize || !ed25519.Verify(pub, signatureData(env.License), sig) {
		return Grant{}, fmt.Errorf("授权签名校验失败，请使用厂家签发的原文件")
	}
	return env.License, nil
}
func TrustedKeys() PublicKeys { var k PublicKeys; _ = json.Unmarshal(embeddedPublic, &k); return k }
func ReadPrivate(path string) (ed25519.PrivateKey, error) {
	b, e := os.ReadFile(path)
	if e != nil {
		return nil, e
	}
	raw, e := hex.DecodeString(strings.TrimSpace(string(b)))
	if e != nil || len(raw) != ed25519.PrivateKeySize {
		return nil, fmt.Errorf("厂家私钥格式无效")
	}
	derived := ed25519.NewKeyFromSeed(raw[:ed25519.SeedSize])
	if !bytes.Equal(raw, derived) {
		return nil, fmt.Errorf("厂家私钥内容无效")
	}
	return ed25519.PrivateKey(raw), nil
}
func InitKey(private, public string) error {
	if _, e := os.Stat(private); e == nil {
		return fmt.Errorf("私钥已存在，禁止覆盖或重新生成；请保留原密钥")
	}
	pub, key, e := ed25519.GenerateKey(rand.Reader)
	if e != nil {
		return e
	}
	if e = os.MkdirAll(filepath.Dir(private), 0700); e != nil {
		return e
	}
	f, e := os.OpenFile(private, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if e != nil {
		return e
	}
	_, e = f.WriteString(hex.EncodeToString(key) + "\n")
	if e == nil {
		e = f.Sync()
	}
	ce := f.Close()
	if e == nil {
		e = ce
	}
	if e != nil {
		return e
	}
	k := PublicKeys{map[string]string{KeyID(pub): hex.EncodeToString(pub)}}
	b, _ := json.MarshalIndent(k, "", "  ")
	return config.WriteAtomic(public, append(b, '\n'))
}

package licensing

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func fixture(t *testing.T) (Request, []byte, PublicKeys, Reader) {
	t.Helper()
	pub, key, e := ed25519.GenerateKey(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	reader := Reader(func(k string) (string, error) {
		if k != "emmc-cid" {
			return "", fmt.Errorf("missing")
		}
		return "0102030405060708090a0b0c0d0e0f10", nil
	})
	req, e := Probe(reader)
	if e != nil {
		t.Fatal(e)
	}
	env, e := NewGrant(req, "测试客户", key)
	if e != nil {
		t.Fatal(e)
	}
	b, _ := json.Marshal(env)
	return req, b, PublicKeys{map[string]string{KeyID(pub): hex.EncodeToString(pub)}}, reader
}
func TestLicenseAuthenticityAndHardware(t *testing.T) {
	_, b, keys, reader := fixture(t)
	p := filepath.Join(t.TempDir(), "license.json")
	c := NewWithKeys(p, true, reader, keys)
	if c.Allowed() || c.Status().FileValid {
		t.Fatal("missing license allowed")
	}
	if e := c.Import(b); e != nil {
		t.Fatal(e)
	}
	if c.Allowed() || !c.Status().RestartRequired {
		t.Fatal("import activated hardware without restart")
	}
	active := NewWithKeys(p, true, reader, keys)
	if !active.Allowed() || !active.Status().Permanent {
		t.Fatal("valid permanent license rejected")
	}
	// Replacing a valid grant keeps the running decision but advertises a restart.
	pub2, key2, _ := ed25519.GenerateKey(rand.Reader)
	keys.Keys[KeyID(pub2)] = hex.EncodeToString(pub2)
	req2, _ := active.Request()
	env2, _ := NewGrant(req2, "新客户记录", key2)
	b2, _ := json.Marshal(env2)
	if e := active.Import(b2); e != nil || !active.Allowed() || !active.Status().RestartRequired {
		t.Fatal("replacement activation semantics", e)
	}
	if e := active.Import(b); e != nil {
		t.Fatal(e)
	}
	other := func(string) (string, error) { return "1112131415161718191a1b1c1d1e1f20", nil }
	if NewWithKeys(p, true, other, keys).Allowed() {
		t.Fatal("copied license accepted on other device")
	}
	if e := active.Import([]byte(strings.Replace(string(b), "测试客户", "伪造客户", 1))); e == nil {
		t.Fatal("tampering accepted")
	}
	if e := active.Import(append(b, []byte(" {}")...)); e == nil {
		t.Fatal("trailing content accepted")
	}
	if e := active.Import([]byte(strings.Repeat("x", Limit+1))); e == nil {
		t.Fatal("oversized license")
	}
	if _, e := VerifySignature(b, PublicKeys{}); e == nil {
		t.Fatal("untrusted issuer")
	}
	os.Remove(p)
	if !active.Allowed() || active.Status().FileValid {
		t.Fatal("file removal should warn for next restart, not stop process")
	}
	if NewWithKeys(p, true, reader, keys).Allowed() {
		t.Fatal("missing license accepted on reboot")
	}
}
func TestPermanentNotClockOrInterfaceBound(t *testing.T) {
	req, _, keys, reader := fixture(t)
	pub, key, _ := ed25519.GenerateKey(rand.Reader)
	keys.Keys[KeyID(pub)] = hex.EncodeToString(pub)
	g, _ := NewGrant(req, "永久", key)
	g.License.IssuedAt = "2099-01-01T00:00:00Z"
	g.Signature = hex.EncodeToString(ed25519.Sign(key, signatureData(g.License)))
	b, _ := json.Marshal(g)
	p := filepath.Join(t.TempDir(), "license.json")
	os.WriteFile(p, b, 0600)
	if !NewWithKeys(p, true, reader, keys).Allowed() {
		t.Fatal("must not expire based on gateway clock")
	}
	// A new firmware may expose a higher-priority ID; existing signed kind remains binding.
	newer := func(k string) (string, error) {
		if k == "soc-serial" {
			return "1234567890abcdef", nil
		}
		return reader(k)
	}
	c := NewWithKeys(p, true, newer, keys)
	r, e := c.Request()
	if e != nil || r.Kind != req.Kind || !c.Allowed() {
		t.Fatal("identity changed after firmware upgrade")
	}
	g.License.Permanent = false
	g.Signature = hex.EncodeToString(ed25519.Sign(key, signatureData(g.License)))
	b, _ = json.Marshal(g)
	if _, e := VerifySignature(b, keys); e == nil {
		t.Fatal("temporary license accepted")
	}
}
func TestHardwareFallbackAndNormalization(t *testing.T) {
	a, e := Fingerprint("soc-serial", " 0xABCDEF0123456789\n")
	b, _ := Fingerprint("soc-serial", "abcdef0123456789")
	if e != nil || a != b {
		t.Fatal("normalization")
	}
	for _, v := range []string{"00000000", "ffffffff", "bad id", "123", "123456789"} {
		if _, e := Fingerprint("cpu-serial", v); e == nil {
			t.Fatal("bad identity", v)
		}
	}
	if _, e := Probe(func(string) (string, error) { return "", fmt.Errorf("not available") }); e == nil {
		t.Fatal("no hardware must fail closed")
	}
	if New("", false, nil).Status().State != "simulation" {
		t.Fatal("simulation must be clearly marked")
	}
}
func TestFactoryKeySafety(t *testing.T) {
	dir := t.TempDir()
	key := filepath.Join(dir, "key")
	pub := filepath.Join(dir, "public.json")
	if e := InitKey(key, pub); e != nil {
		t.Fatal(e)
	}
	before, _ := os.ReadFile(key)
	if e := InitKey(key, pub); e == nil {
		t.Fatal("overwrote factory key")
	}
	after, _ := os.ReadFile(key)
	if string(before) != string(after) {
		t.Fatal("key changed")
	}
	if _, e := ReadPrivate(key); e != nil {
		t.Fatal(e)
	}
}

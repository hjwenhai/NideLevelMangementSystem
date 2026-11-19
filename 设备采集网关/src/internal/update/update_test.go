package update

import (
	"archive/tar"
	"archive/zip"
	"bytes"
	"compress/gzip"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func put(t *testing.T, p string, b []byte) {
	t.Helper()
	if e := os.MkdirAll(filepath.Dir(p), 0700); e != nil {
		t.Fatal(e)
	}
	if e := os.WriteFile(p, b, 0600); e != nil {
		t.Fatal(e)
	}
}
func runtimeStage(t *testing.T, dir, release, arch string, trust []byte) string {
	t.Helper()
	stage := filepath.Join(dir, "gt6757s-"+release+"-"+arch)
	for _, n := range RuntimeFiles {
		if n == "SHA256SUMS" {
			continue
		}
		b := []byte("#!/bin/sh\nexit 0\n")
		switch n {
		case "RELEASE":
			b = []byte(release + "\n")
		case "ARCH":
			b = []byte(arch + "\n")
		case "NORTH_VERSION":
			b = []byte("3.2\n")
		case "UPDATE_VERSION":
			b = []byte("1\n")
		case "config.default.json":
			b = []byte("{}\n")
		case "deploy/update-trust.json":
			b = trust
		}
		if strings.HasPrefix(n, "bin/") {
			b = make([]byte, 64)
			if arch == "windows-amd64-test" {
				copy(b, "MZ")
			} else {
				copy(b, "\x7fELF")
				b[4] = 2
				b[5] = 1
				b[18] = 183
				if arch == "linux-armv7" {
					b[4] = 1
					b[18] = 40
				}
			}
		}
		put(t, filepath.Join(stage, n), b)
	}
	var sums strings.Builder
	for _, n := range RuntimeFiles {
		if n != "SHA256SUMS" {
			h, _ := HashFile(filepath.Join(stage, n))
			fmt.Fprintf(&sums, "%s  %s\n", h, n)
		}
	}
	put(t, filepath.Join(stage, "SHA256SUMS"), []byte(sums.String()))
	return stage
}
func makePayload(t *testing.T, stage, out, attack string) {
	t.Helper()
	f, e := os.Create(out)
	if e != nil {
		t.Fatal(e)
	}
	gz := gzip.NewWriter(f)
	tw := tar.NewWriter(gz)
	for i, n := range RuntimeFiles {
		b, e := os.ReadFile(filepath.Join(stage, n))
		if e != nil {
			t.Fatal(e)
		}
		h := &tar.Header{Name: filepath.Base(stage) + "/" + n, Mode: 0755, Size: int64(len(b)), Typeflag: tar.TypeReg}
		if i == 0 {
			switch attack {
			case "traversal":
				h.Name = filepath.Base(stage) + "/../outside"
			case "symlink":
				h.Typeflag = tar.TypeSymlink
				h.Linkname = "/etc/passwd"
				h.Size = 0
			case "unexpected":
				h.Name = filepath.Base(stage) + "/extra.sh"
			}
		}
		if e = tw.WriteHeader(h); e != nil {
			t.Fatal(e)
		}
		if h.Size > 0 {
			if _, e = tw.Write(b); e != nil {
				t.Fatal(e)
			}
		}
		if i == 0 && attack == "duplicate" {
			if e = tw.WriteHeader(h); e != nil {
				t.Fatal(e)
			}
			if _, e = tw.Write(b); e != nil {
				t.Fatal(e)
			}
		}
	}
	if e = tw.Close(); e != nil {
		t.Fatal(e)
	}
	if e = gz.Close(); e != nil {
		t.Fatal(e)
	}
	f.Close()
}

type fixture struct{ root, key, trust, stage, payload, bundle, arch string }

func newFixture(t *testing.T, arch, attack string) fixture {
	t.Helper()
	dir := t.TempDir()
	key, trust := filepath.Join(dir, "private.key"), filepath.Join(dir, "public.json")
	if e := InitSigningKey(key, trust); e != nil {
		t.Fatal(e)
	}
	pub, _ := os.ReadFile(trust)
	stage := runtimeStage(t, dir, "0.6.0-20261029-r2", arch, pub)
	payload := filepath.Join(dir, "runtime.tar.gz")
	makePayload(t, stage, payload, attack)
	bundle := filepath.Join(dir, "valid.gwup")
	if e := SignBundle(payload, stage, bundle, key, "测试更新"); e != nil {
		t.Fatal(e)
	}
	return fixture{dir, key, trust, stage, payload, bundle, arch}
}
func rewriteZip(t *testing.T, src, out, entry string, change func([]byte) []byte, extra bool) {
	t.Helper()
	z, e := zip.OpenReader(src)
	if e != nil {
		t.Fatal(e)
	}
	defer z.Close()
	f, _ := os.Create(out)
	w := zip.NewWriter(f)
	for _, v := range z.File {
		r, _ := v.Open()
		b, _ := io.ReadAll(r)
		r.Close()
		if v.Name == entry {
			b = change(b)
		}
		o, _ := w.CreateHeader(&zip.FileHeader{Name: v.Name, Method: zip.Store})
		o.Write(b)
	}
	if extra {
		o, _ := w.CreateHeader(&zip.FileHeader{Name: "unexpected", Method: zip.Store})
		o.Write([]byte("x"))
	}
	w.Close()
	f.Close()
}
func TestBundleTrustAndBoundedRuntime(t *testing.T) {
	for _, arch := range []string{"linux-arm64", "linux-armv7"} {
		t.Run(arch, func(t *testing.T) {
			x := newFixture(t, arch, "")
			dest := filepath.Join(x.root, "unpacked")
			if e := os.Mkdir(dest, 0700); e != nil {
				t.Fatal(e)
			}
			v, e := VerifyBundle(x.bundle, x.trust, arch, dest)
			if e != nil || v.Manifest.Release != "0.6.0-20261029-r2" {
				t.Fatal(v, e)
			}
			for _, n := range RuntimeFiles {
				a, _ := os.ReadFile(filepath.Join(x.stage, n))
				b, _ := os.ReadFile(filepath.Join(dest, n))
				if !bytes.Equal(a, b) {
					t.Fatal(n)
				}
			}
			if _, e = VerifyBundle(x.bundle, x.trust, arch, dest); e == nil {
				t.Fatal("extraction overwrote files")
			}
		})
	}
	for _, attack := range []string{"traversal", "symlink", "duplicate", "unexpected"} {
		t.Run(attack, func(t *testing.T) {
			x := newFixture(t, "linux-arm64", attack)
			if _, e := VerifyBundle(x.bundle, x.trust, x.arch, ""); e == nil {
				t.Fatal("malformed tar accepted")
			}
		})
	}
	x := newFixture(t, "linux-arm64", "")
	for _, entry := range []string{"manifest.json", "signature.ed25519", "runtime.tar.gz"} {
		t.Run("tamper-"+entry, func(t *testing.T) {
			out := filepath.Join(x.root, entry+".gwup")
			rewriteZip(t, x.bundle, out, entry, func(b []byte) []byte { b[len(b)/2] ^= 1; return b }, false)
			if _, e := VerifyBundle(out, x.trust, x.arch, ""); e == nil {
				t.Fatal("tampered bundle accepted")
			}
		})
	}
	t.Run("extra-zip", func(t *testing.T) {
		out := filepath.Join(x.root, "extra.gwup")
		rewriteZip(t, x.bundle, out, "", func(b []byte) []byte { return b }, true)
		if _, e := VerifyBundle(out, x.trust, x.arch, ""); e == nil {
			t.Fatal("extra accepted")
		}
	})
	t.Run("wrong-key", func(t *testing.T) {
		other := newFixture(t, "linux-arm64", "")
		if _, e := VerifyBundle(x.bundle, other.trust, x.arch, ""); e == nil {
			t.Fatal("wrong key accepted")
		}
	})
	t.Run("wrong-architecture", func(t *testing.T) {
		if _, e := VerifyBundle(x.bundle, x.trust, "linux-armv7", ""); e == nil {
			t.Fatal("wrong arch accepted")
		}
	})
	t.Run("raw-tgz", func(t *testing.T) {
		if _, e := VerifyBundle(x.payload, x.trust, x.arch, ""); e == nil {
			t.Fatal("raw tar accepted")
		}
	})
	t.Run("manifest-compatibility", func(t *testing.T) {
		v, _ := VerifyBundle(x.bundle, x.trust, x.arch, "")
		for _, field := range []string{"product", "north", "schema", "protocol", "size", "name", "version"} {
			m := v.Manifest
			m.Files = append([]File(nil), m.Files...)
			switch field {
			case "product":
				m.Product = "Other"
			case "north":
				m.North = "3.1"
			case "schema":
				m.DatabaseSchema = 4
			case "protocol":
				m.UpdateProtocol = 2
			case "size":
				m.UnpackedSize = ExpandLimit + 1
			case "name":
				m.Files[0].Name = "../outside"
			case "version":
				m.Software = "0.5.0"
			}
			if m.Validate(x.arch) == nil {
				t.Fatal(field)
			}
		}
	})
}
func TestReleaseOrderingAndKeys(t *testing.T) {
	for _, p := range [][2]string{{"0.6.0-20261029-r2", "0.6.0-20261029-r1"}, {"0.7.0-20261029-r1", "0.6.9-20261029-r9"}, {"1.0.0-20261029-r1", "0.9.99-20261029-r9"}} {
		n, e := CompareRelease(p[0], p[1])
		if e != nil || n <= 0 {
			t.Fatal(p, e)
		}
	}
	for _, v := range []string{"../x", "0.6.0-20260230-r1", "0.6.0-20261029-r0", "00.6.0-20261029-r1"} {
		if _, e := releaseParts(v); e == nil {
			t.Fatal(v)
		}
	}
	x := newFixture(t, "linux-arm64", "")
	a, _ := os.ReadFile(x.key)
	if InitSigningKey(x.key, x.trust) == nil {
		t.Fatal("overwrote key")
	}
	b, _ := os.ReadFile(x.key)
	if !bytes.Equal(a, b) {
		t.Fatal("key changed")
	}
	h := sha256.Sum256(a)
	if len(hex.EncodeToString(h[:])) != 64 {
		t.Fatal("hash")
	}
}
func serviceFixture(t *testing.T) (*Service, fixture) {
	t.Helper()
	if runtime.GOOS != "windows" {
		t.Skip("isolated Windows fixture; signed Linux package tests run on all hosts")
	}
	x := newFixture(t, "windows-amd64-test", "")
	base := filepath.Join(x.root, "base")
	pub, _ := os.ReadFile(x.trust)
	old := runtimeStage(t, filepath.Join(base, "releases"), "0.6.0-20261029-r1", x.arch, pub)
	installed := filepath.Join(base, "releases", "0.6.0-20261029-r1")
	if e := os.Rename(old, installed); e != nil {
		t.Fatal(e)
	}
	old = installed
	if e := os.Symlink(old, filepath.Join(base, "current")); e != nil {
		t.Fatal(e)
	}
	for n, b := range map[string][]byte{"config/update-trust.json": pub, "config/gateway.json": []byte("{\"site\":true}"), "config/service.env": []byte("WEB_LISTEN=site\n"), "data/admin.json": []byte("{\"hash\":\"keep\"}"), "data/gateway-v2.db": []byte("live database")} {
		put(t, filepath.Join(base, n), b)
	}
	home, _ := os.UserHomeDir()
	shell := filepath.Join(home, ".cache/codex-runtimes/codex-primary-runtime/dependencies/native/git/usr/bin/sh.exe")
	s, e := New(Options{Base: base, Fixture: true, Shell: shell})
	if e != nil {
		t.Fatal(e)
	}
	return s, x
}
func TestStagingLocksAndDurableRecovery(t *testing.T) {
	s, x := serviceFixture(t)
	b, _ := os.ReadFile(x.bundle)
	ready, e := s.Stage(context.Background(), bytes.NewReader(b))
	if e != nil || ready.Phase != "ready" || s.BlocksWrites() {
		t.Fatal(ready, e)
	}
	if _, e = s.Stage(context.Background(), strings.NewReader("invalid")); e == nil || s.Status().ID != ready.ID {
		t.Fatal("bad upload replaced staged package")
	}
	os.Mkdir(s.path("data", "upgrades", "upload.lock"), 0700)
	if s.Begin(ready.ID, "web:test") == nil {
		t.Fatal("overlapping upload allowed")
	}
	os.Remove(s.path("data", "upgrades", "upload.lock"))
	if e = s.Begin(ready.ID, "web:test"); e != nil || !s.BlocksWrites() {
		t.Fatal(e)
	}
	if s.Begin(ready.ID, "web:test") == nil {
		t.Fatal("duplicate confirmed")
	}
	if _, e = s.Stage(context.Background(), bytes.NewReader(b)); e == nil {
		t.Fatal("upload while upgrading")
	}
	st, _ := s.read()
	st.BootID = "earlier-boot"
	s.save(st)
	if e = s.ReconcileBoot(); e != nil || s.Status().Phase != "needs_attention" || !s.BlocksWrites() {
		t.Fatal(e, s.Status())
	}
	put(t, s.path("data", "upgrades", "state.json"), []byte("corrupt"))
	if !s.BlocksWrites() {
		t.Fatal("corrupt state fails open")
	}
}
func TestRecoveryExecutionOwnership(t *testing.T) {
	s, x := serviceFixture(t)
	b, _ := os.ReadFile(x.bundle)
	st, e := s.Stage(context.Background(), bytes.NewReader(b))
	if e != nil {
		t.Fatal(e)
	}
	if e = s.Begin(st.ID, "web:test"); e != nil {
		t.Fatal(e)
	}
	if RunRecovery(s.Opts.Base, st.ID) == nil {
		t.Fatal("recovery interrupted a newly queued worker")
	}
	release, e := s.execution(st.ID, false)
	if e != nil {
		t.Fatal(e)
	}
	if _, e = s.execution(st.ID, true); e == nil {
		t.Fatal("reclaimed a live execution owner")
	}
	release()
	job, _ := s.job(st.ID)
	if e = os.Mkdir(filepath.Join(job, "execute.lock"), 0700); e != nil {
		t.Fatal(e)
	}
	if _, e = s.execution(st.ID, true); e == nil {
		t.Fatal("reclaimed unknown lock ownership")
	}
	if !s.BlocksWrites() {
		t.Fatal("lost maintenance gate")
	}
}
func TestWorkerTransactionAndCompatibleRollback(t *testing.T) {
	for _, failure := range []string{"", "config", "backup", "start", "probe", "recovery"} {
		t.Run("failure-"+failure, func(t *testing.T) {
			s, x := serviceFixture(t)
			b, _ := os.ReadFile(x.bundle)
			st, e := s.Stage(context.Background(), bytes.NewReader(b))
			if e != nil {
				t.Fatal(e)
			}
			if e = s.Begin(st.ID, "web:test"); e != nil {
				t.Fatal(e)
			}
			var calls []string
			ops := WorkerOps{Service: func(ctx context.Context, rel, action string) error {
				calls = append(calls, rel+":"+action)
				if failure == "start" && rel == st.Target && action == "start" {
					return fmt.Errorf("injected start failure")
				}
				return nil
			}, CheckConfig: func(context.Context, string) error {
				if failure == "config" {
					return fmt.Errorf("injected config failure")
				}
				return nil
			}, Backup: func(src, dest string) error {
				if failure == "backup" {
					return fmt.Errorf("injected backup failure")
				}
				b, _ := os.ReadFile(src)
				return os.WriteFile(dest, b, 0600)
			}, Probe: func(ctx context.Context, rel, id string) error {
				if !s.BlocksWrites() {
					t.Fatal("gate released before verification")
				}
				if failure == "recovery" || failure == "probe" && rel == st.Target {
					return fmt.Errorf("injected probe failure")
				}
				return nil
			}}
			e = s.Run(st.ID, &ops)
			status := s.Status()
			want := "succeeded"
			if failure == "config" {
				want = "failed"
			} else if failure == "recovery" {
				want = "needs_attention"
			} else if failure != "" {
				want = "rolled_back"
			}
			if status.Phase != want || (e == nil) != (failure == "") {
				t.Fatalf("%s %v", status.Phase, e)
			}
			if s.BlocksWrites() != (failure == "recovery") {
				t.Fatal("gate", status)
			}
			db, _ := os.ReadFile(s.path("data", "gateway-v2.db"))
			if string(db) != "live database" {
				t.Fatal("database restored or lost")
			}
			key, _ := os.ReadFile(s.path("config", "update-trust.json"))
			orig, _ := os.ReadFile(x.trust)
			if !bytes.Equal(key, orig) {
				t.Fatal("trust changed")
			}
			if failure == "config" && len(calls) > 0 {
				t.Fatal("stopped old service before config verification")
			}
			if failure != "config" {
				if _, e = os.Stat(s.path("data", "upgrade-backup-"+st.ID, "admin.json")); e != nil {
					t.Fatal("account backup", e)
				}
			}
			if s.Run(st.ID, &ops) == nil {
				t.Fatal("worker replayed completed job")
			}
		})
	}
}

//go:build licensebridge

package update

import (
	"bytes"
	"context"
	"os"
	"path/filepath"
	"testing"
)

func TestBridgePriorRollback(t *testing.T) {
	s, x := serviceFixture(t)
	original, _ := os.ReadFile(x.bundle)
	if _, err := s.StageBridgeTarget(context.Background(), bytes.NewReader(original)); err == nil {
		t.Fatal("wrong target accepted")
	}
	pub, _ := os.ReadFile(x.trust)
	stage := runtimeStage(t, x.root, bridgeTarget, x.arch, pub)
	makePayload(t, stage, x.payload, "")
	x.bundle = filepath.Join(x.root, "next.gwup")
	if err := SignBundle(x.payload, stage, x.bundle, x.key, "bridge retry"); err != nil {
		t.Fatal(err)
	}
	data, err := os.ReadFile(x.bundle)
	if err != nil {
		t.Fatal(err)
	}
	runStage := func() error { _, err := s.Stage(context.Background(), bytes.NewReader(data)); return err }
	if err := runStage(); err != nil {
		t.Fatal("fresh path", err)
	}
	target := s.path("releases", bridgeTarget)
	if err := os.Rename(stage, target); err != nil {
		t.Fatal(err)
	}
	if err := runStage(); err != nil {
		t.Fatal("matching existing target", err)
	}
	extra := filepath.Join(target, "extra")
	put(t, extra, []byte("unexpected"))
	if runStage() == nil {
		t.Fatal("accepted extra file")
	}
	if err := os.Remove(extra); err != nil {
		t.Fatal(err)
	}
	put(t, filepath.Join(target, "bin", "gateway-web"), []byte("damaged"))
	if runStage() == nil {
		t.Fatal("accepted changed executable")
	}
}

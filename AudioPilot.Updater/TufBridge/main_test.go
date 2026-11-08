package main

import (
	"crypto"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/sigstore/sigstore/pkg/signature"
	"github.com/theupdateframework/go-tuf/v2/metadata"
)

func setupRepository(t *testing.T) (*repositoryFixture, request) {
	t.Helper()
	f, err := newFixture()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(f.dispose)
	root := t.TempDir()
	req := request{Schema: 1, Operation: "verify", Nonce: strings.Repeat("A", 64), Repository: filepath.Join(root, "repository"), Cache: filepath.Join(root, "cache"), Target: "candidate.zip"}
	custom, err := json.Marshal(targetIdentity{Product: "AudioPilot", Channel: "local-test", Version: "1.0.1", Kind: "portable", Architecture: "win-x64", Runtime: "self-contained", MinimumHelperVersion: "1.0.0"})
	if err != nil {
		t.Fatal(err)
	}
	raw := json.RawMessage(custom)
	sum := sha256.Sum256([]byte("package"))
	target := metadata.TargetFile()
	target.Length = 7
	target.Hashes = metadata.Hashes{"sha256": sum[:]}
	target.Custom = &raw
	f.targets.Signed.Targets[req.Target] = target
	writeFixture(t, f, req.Repository)
	data, err := os.ReadFile(filepath.Join(req.Repository, "bootstrap.root.json"))
	if err != nil {
		t.Fatal(err)
	}
	pin := sha256.Sum256(data)
	req.BootstrapHash = strings.ToUpper(hex.EncodeToString(pin[:]))
	return f, req
}

func TestProductionBridgeRejectsAcceptanceTransportEvenWithSandboxEnvironment(t *testing.T) {
	_, req := setupRepository(t)
	oldRoot, oldAcceptance := stableRoot, sandboxAcceptance
	t.Cleanup(func() { stableRoot, sandboxAcceptance = oldRoot, oldAcceptance })
	stableRoot, sandboxAcceptance = req.BootstrapHash, ""
	req.Transport, req.Origin = "stable-test", "https://127.0.0.1:45123/"
	t.Setenv("USERNAME", "WDAGUtilityAccount")
	if _, err := verify(req); err == nil || !strings.Contains(err.Error(), "Sandbox-only and root-bound") {
		t.Fatal("production bridge accepted an uncompiled transport", err)
	}
	sandboxAcceptance = "1"
	t.Setenv("USERNAME", "ordinary-user")
	if _, err := verify(req); err == nil || !strings.Contains(err.Error(), "Sandbox-only and root-bound") {
		t.Fatal("acceptance bridge opened outside its guest", err)
	}
}
func writeFixture(t *testing.T, f *repositoryFixture, repo string) {
	t.Helper()
	if err := f.write(repo); err != nil {
		t.Fatal(err)
	}
}
func requireVerified(t *testing.T, req request) response {
	t.Helper()
	result, err := verify(req)
	if err != nil {
		t.Fatal(err)
	}
	if result.Target != req.Target || result.Length != 7 || result.Custom.Version != "1.0.1" {
		t.Fatalf("unexpected target: %+v", result)
	}
	return result
}
func requireRejected(t *testing.T, req request) {
	t.Helper()
	if _, err := verify(req); err == nil {
		t.Fatal("untrusted metadata was accepted")
	}
}
func TestCompleteWorkflowAndPersistentCache(t *testing.T) {
	_, req := setupRepository(t)
	first := requireVerified(t, req)
	second := requireVerified(t, req)
	if first.SHA256 != second.SHA256 {
		t.Fatal("cached identity changed")
	}
	bootstrap, _ := os.ReadFile(filepath.Join(req.Repository, "bootstrap.root.json"))
	store, err := openTrustStore(req.Cache, req.BootstrapHash, bootstrap)
	if err != nil {
		t.Fatal(err)
	}
	defer store.file.Close()
	for _, role := range []string{"root", "timestamp", "snapshot", "targets"} {
		if len(store.latest[role]) == 0 {
			t.Fatalf("missing %s", role)
		}
	}
}

func TestPublisherBindingsAreTypedAndUnknownCustomFieldsStillFail(t *testing.T) {
	f, req := setupRepository(t)
	target := f.targets.Signed.Targets[req.Target]
	var custom map[string]any
	if err := json.Unmarshal(*target.Custom, &custom); err != nil {
		t.Fatal(err)
	}
	custom["windowsPublisherSha256"] = strings.Repeat("A", 64)
	custom["windowsTrust"] = "self-signed-staging"
	setCustom := func() {
		data, err := json.Marshal(custom)
		if err != nil {
			t.Fatal(err)
		}
		raw := json.RawMessage(data)
		target.Custom = &raw
		writeFixture(t, f, req.Repository)
	}
	setCustom()
	result := requireVerified(t, req)
	if result.Custom.WindowsPublisherSHA256 != strings.Repeat("A", 64) || result.Custom.WindowsTrust != "self-signed-staging" {
		t.Fatal("bridge discarded publisher bindings")
	}
	custom["unexpectedPublisherOverride"] = "must be rejected"
	f.targets.Signed.Version++
	f.snapshot.Signed.Version++
	f.timestamp.Signed.Version++
	setCustom()
	if _, err := verify(req); err == nil || !strings.Contains(err.Error(), `unknown field "unexpectedPublisherOverride"`) {
		t.Fatalf("strict custom contract did not reject the field: %v", err)
	}
}
func TestUntrustedMetadataRejected(t *testing.T) {
	for _, kind := range []string{"bootstrap", "tamper", "expired-timestamp", "expired-targets", "snapshot-mix", "missing-root-cache", "cache-pin"} {
		t.Run(kind, func(t *testing.T) {
			f, req := setupRepository(t)
			switch kind {
			case "bootstrap":
				req.BootstrapHash = strings.Repeat("0", 64)
			case "tamper":
				path := filepath.Join(req.Repository, "metadata", "1.targets.json")
				data, err := os.ReadFile(path)
				if err != nil {
					t.Fatal(err)
				}
				data = []byte(strings.Replace(string(data), "1.0.1", "9.9.9", 1))
				if err = os.WriteFile(path, data, 0600); err != nil {
					t.Fatal(err)
				}
			case "expired-timestamp":
				f.timestamp.Signed.Expires = time.Now().Add(-time.Hour)
				writeFixture(t, f, req.Repository)
			case "expired-targets":
				f.targets.Signed.Expires = time.Now().Add(-time.Hour)
				writeFixture(t, f, req.Repository)
			case "snapshot-mix":
				f.targets.Signed.Version = 2
				writeFixture(t, f, req.Repository)
				data, err := os.ReadFile(filepath.Join(req.Repository, "metadata", "1.targets.json"))
				if err != nil {
					t.Fatal(err)
				}
				if err = os.WriteFile(filepath.Join(req.Repository, "metadata", "2.targets.json"), data, 0600); err != nil {
					t.Fatal(err)
				}
			case "missing-root-cache":
				requireVerified(t, req)
				if err := os.Remove(filepath.Join(req.Cache, "trust.journal")); err != nil {
					t.Fatal(err)
				}
			case "cache-pin":
				requireVerified(t, req)
				if err := os.WriteFile(filepath.Join(req.Cache, "trust.journal"), []byte(strings.Repeat("0", 64)), 0600); err != nil {
					t.Fatal(err)
				}
			}
			requireRejected(t, req)
		})
	}
}
func TestTimestampRollbackRejected(t *testing.T) {
	f, req := setupRepository(t)
	old, err := os.ReadFile(filepath.Join(req.Repository, "metadata", "timestamp.json"))
	if err != nil {
		t.Fatal(err)
	}
	f.timestamp.Signed.Version = 2
	writeFixture(t, f, req.Repository)
	requireVerified(t, req)
	if err = os.WriteFile(filepath.Join(req.Repository, "metadata", "timestamp.json"), old, 0600); err != nil {
		t.Fatal(err)
	}
	requireRejected(t, req)
}
func TestRootRotationAndRevokedTargetsKey(t *testing.T) {
	f, req := setupRepository(t)
	requireVerified(t, req)
	oldRoot := append(ed25519.PrivateKey(nil), f.keys["root"]...)
	oldTargets := append(ed25519.PrivateKey(nil), f.keys["targets"]...)
	defer clear(oldRoot)
	defer clear(oldTargets)
	for _, role := range []string{"root", "targets"} {
		for _, id := range append([]string(nil), f.root.Signed.Roles[role].KeyIDs...) {
			if err := f.root.Signed.RevokeKey(id, role); err != nil {
				t.Fatal(err)
			}
		}
		_, key, err := ed25519.GenerateKey(nil)
		if err != nil {
			t.Fatal(err)
		}
		clear(f.keys[role])
		f.keys[role] = key
		pub, err := metadata.KeyFromPublicKey(key.Public())
		if err != nil {
			t.Fatal(err)
		}
		if err = f.root.Signed.AddKey(pub, role); err != nil {
			t.Fatal(err)
		}
	}
	f.root.Signed.Version = 2
	f.targets.Signed.Version = 2
	f.snapshot.Signed.Version = 2
	f.timestamp.Signed.Version = 2
	writeFixture(t, f, req.Repository)
	oldSigner, err := signature.LoadSigner(oldRoot, crypto.Hash(0))
	if err != nil {
		t.Fatal(err)
	}
	if _, err = f.root.Sign(oldSigner); err != nil {
		t.Fatal(err)
	}
	if err = f.root.ToFile(filepath.Join(req.Repository, "metadata", "2.root.json"), true); err != nil {
		t.Fatal(err)
	}
	requireVerified(t, req)
	// The next invocation must start from cached root 2, even if rotation files disappear.
	if err = os.Remove(filepath.Join(req.Repository, "metadata", "2.root.json")); err != nil {
		t.Fatal(err)
	}
	requireVerified(t, req)
	clear(f.keys["targets"])
	f.keys["targets"] = append(ed25519.PrivateKey(nil), oldTargets...)
	f.targets.Signed.Version = 3
	f.snapshot.Signed.Version = 3
	f.timestamp.Signed.Version = 3
	writeFixture(t, f, req.Repository)
	if err = os.Remove(filepath.Join(req.Repository, "metadata", "2.root.json")); err != nil {
		t.Fatal(err)
	}
	requireRejected(t, req)
}
func TestLocalFetcherCannotReachNetworkOrEscape(t *testing.T) {
	_, req := setupRepository(t)
	fetcher := localFetcher{req.Repository}
	for _, location := range []string{"https://example.com/metadata/root.json", "http://audiopilot-local.invalid/metadata/root.json", "https://audiopilot-local.invalid/metadata/../bootstrap.root.json", "https://audiopilot-local.invalid/metadata/%2e%2e%2fbootstrap.root.json"} {
		if _, err := fetcher.DownloadFile(location, 65536, time.Second); err == nil {
			t.Fatalf("accepted %s", location)
		}
	}
}

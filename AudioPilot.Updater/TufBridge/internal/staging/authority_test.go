package staging

import (
	"bytes"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	"github.com/theupdateframework/go-tuf/v2/metadata"
	"github.com/theupdateframework/go-tuf/v2/metadata/config"
	"github.com/theupdateframework/go-tuf/v2/metadata/updater"
)

func TestEncryptedBackupAuthenticatesAndPreservesRecoveryFloor(t *testing.T) {
	req, initial := authority(t)
	req.Packages = []Package{candidate(t, "1.0.1")}
	if _, err := Execute(req); err != nil {
		t.Fatal(err)
	}
	backup := Request{Operation: "backup", Repository: req.Repository, BootstrapHash: req.BootstrapHash,
		Backup: filepath.Join(t.TempDir(), "authority.age"), Passphrase: "temporary test passphrase, not a real authority", Keys: initial.Keys}
	invalid := map[string]string{}
	for role, seed := range initial.Keys {
		invalid[role] = seed
	}
	delete(invalid, "root")
	invalid["root-../../outside"] = initial.Keys["root"]
	if err := validateCustodyKeys(backup, invalid); err == nil {
		t.Fatal("unsafe backup key label accepted")
	}
	if _, err := Execute(backup); err != nil {
		t.Fatal(err)
	}
	ciphertext, err := os.ReadFile(backup.Backup)
	if err != nil {
		t.Fatal(err)
	}
	for _, seed := range initial.Keys {
		if bytes.Contains(ciphertext, []byte(seed)) {
			t.Fatal("plaintext seed in encrypted backup")
		}
	}
	backup.Operation = "restore"
	backup.Keys = nil
	restored, err := Execute(backup)
	if err != nil || restored.Keys["root"] != initial.Keys["root"] || restored.Version != 1 {
		t.Fatalf("restore: %+v %v", restored.Version, err)
	}
	wrong := backup
	wrong.Passphrase = "a different temporary passphrase"
	if _, err = Execute(wrong); err == nil {
		t.Fatal("wrong password accepted")
	}
	wrong = backup
	wrong.MinimumVersion = 2
	if _, err = Execute(wrong); err == nil {
		t.Fatal("recovery floor reset accepted")
	}
	destination := filepath.Join(t.TempDir(), "rotated")
	rotated, err := Execute(Request{Operation: "rotate", Repository: req.Repository, BootstrapHash: req.BootstrapHash,
		Destination: destination, RootSigners: []string{initial.Keys["root"]}})
	if err != nil || rotated.RootVersion != 2 {
		t.Fatal("prepare revoked-backup test", err)
	}
	wrong = backup
	wrong.Repository = destination
	// A valid archive is not authority to revive revoked keys, even with the original bootstrap pin.
	if _, err = Execute(wrong); err == nil {
		t.Fatal("backup revived revoked keys")
	}
	if _, err = Execute(Request{Operation: "check", Repository: req.Repository, BootstrapHash: req.BootstrapHash, MinimumRootVersion: 2}); err == nil {
		t.Fatal("root recovery floor reset accepted")
	}
	ciphertext[len(ciphertext)-1] ^= 1
	if err = os.WriteFile(backup.Backup, ciphertext, 0600); err != nil {
		t.Fatal(err)
	}
	if _, err = Execute(backup); err == nil {
		t.Fatal("tampered backup accepted")
	}
}

func TestRotationThresholdsRevocationAndInterruptedPublication(t *testing.T) {
	req, initial := authority(t)
	req.Packages = []Package{candidate(t, "1.0.1")}
	if _, err := Execute(req); err != nil {
		t.Fatal(err)
	}
	root, err := os.ReadFile(filepath.Join(req.Repository, "bootstrap.root.json"))
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := config.New("https://staging.invalid/metadata/", root)
	if err != nil {
		t.Fatal(err)
	}
	cfg.LocalMetadataDir = t.TempDir()
	cfg.LocalTargetsDir = t.TempDir()
	cfg.Fetcher = localFiles(req.Repository)
	client, err := updater.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err = client.Refresh(); err != nil {
		t.Fatal(err)
	}
	rotation := Request{Operation: "rotate", Repository: req.Repository, BootstrapHash: req.BootstrapHash,
		Destination: filepath.Join(t.TempDir(), "rotated"), RootSigners: []string{initial.Keys["root"]}}
	result, err := Execute(rotation)
	if err != nil || result.RootVersion != 2 || result.RootThreshold != 2 || result.Version != 2 {
		t.Fatalf("rotation: %v %v", result.Version, err)
	}
	original, err := Execute(Request{Operation: "check", Repository: req.Repository, BootstrapHash: req.BootstrapHash})
	if err != nil || original.RootVersion != 1 || original.Version != 1 {
		t.Fatal("rotation modified the source", err)
	}
	// A root published before its new catalog must fail closed, then recover without resetting the client's trust cache.
	newRoot, err := os.ReadFile(filepath.Join(rotation.Destination, "metadata", "2.root.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(filepath.Join(req.Repository, "metadata", "2.root.json"), newRoot, 0600); err != nil {
		t.Fatal(err)
	}
	if err = client.Refresh(); err == nil {
		t.Fatal("mixed old catalog/new root accepted")
	}
	cfg.Fetcher = localFiles(rotation.Destination)
	client, err = updater.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err = client.Refresh(); err != nil {
		t.Fatal("interrupted publication did not recover", err)
	}
	if _, err = client.GetTargetInfo(req.Packages[0].Name); err != nil {
		t.Fatal(err)
	}
	revoked := Request{Operation: "renew", Repository: rotation.Destination, BootstrapHash: req.BootstrapHash, Keys: req.Keys}
	if _, err = Execute(revoked); err == nil {
		t.Fatal("revoked online keys accepted")
	}
	second := Request{Operation: "rotate", Repository: rotation.Destination, BootstrapHash: req.BootstrapHash,
		Destination: filepath.Join(t.TempDir(), "next"), RootSigners: []string{result.Keys["root-1"]}}
	if _, err = Execute(second); err == nil {
		t.Fatal("one signer satisfied a two-key threshold")
	}
	if _, err = os.Stat(second.Destination); !os.IsNotExist(err) {
		t.Fatal("failed threshold wrote a candidate")
	}
	second.RootSigners = append(second.RootSigners, result.Keys["root-2"])
	third, err := Execute(second)
	if err != nil || third.RootVersion != 3 {
		t.Fatal("second threshold rotation", err)
	}
	cfg.Fetcher = localFiles(second.Destination)
	client, err = updater.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err = client.Refresh(); err != nil {
		t.Fatal("missed root-chain update", err)
	}
	// Keeping only old-authority signatures cannot satisfy the replacement root.
	md, _, err := load[metadata.RootType](filepath.Join(second.Destination, "metadata", "3.root.json"))
	if err != nil {
		t.Fatal(err)
	}
	roots, err := trustedRoots(Request{Repository: second.Destination, BootstrapHash: req.BootstrapHash})
	if err != nil {
		t.Fatal(err)
	}
	md.Signatures = md.Signatures[:2]
	if roots[2].VerifyDelegate("root", md) == nil {
		t.Fatal("replacement threshold accepted only old signers")
	}
}

func TestOnlineRenewalCannotChangeOrExtendApprovedTargets(t *testing.T) {
	req, _ := authority(t)
	req.Packages = []Package{candidate(t, "1.0.1")}
	if _, err := Execute(req); err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(req.Repository, "metadata", "1.targets.json")
	before, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	req.Operation = "renew"
	req.Packages = nil
	delete(req.Keys, "targets")
	if _, err = Execute(req); err != nil {
		t.Fatal(err)
	}
	after, err := os.ReadFile(path)
	if err != nil || !bytes.Equal(before, after) {
		t.Fatal("online renewal changed approval bytes", err)
	}
	if _, err = os.Stat(filepath.Join(req.Repository, "metadata", "2.targets.json")); !os.IsNotExist(err) {
		t.Fatal("online renewal created new target approval")
	}
	verified, err := Execute(Request{Operation: "check", Repository: req.Repository, BootstrapHash: req.BootstrapHash, MetadataOnly: true})
	if err != nil || verified.Version != 2 || !verified.MetadataOnly || len(verified.Expires) != 4 {
		t.Fatal("metadata health", err)
	}
	if _, err = publish(req, time.Now().Add(30*24*time.Hour)); err == nil {
		t.Fatal("renewal bypassed expiring manual target approval")
	}
	var targets metadata.Metadata[metadata.TargetsType]
	if err = json.Unmarshal(after, &targets); err != nil {
		t.Fatal(err)
	}
	if !targets.Signed.Expires.Equal(verified.Expires["targets"]) {
		t.Fatal("targets expiry changed")
	}
}

package staging

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"github.com/theupdateframework/go-tuf/v2/metadata"
	"github.com/theupdateframework/go-tuf/v2/metadata/config"
	"github.com/theupdateframework/go-tuf/v2/metadata/updater"
)

func authority(t *testing.T) (Request, Result) {
	t.Helper()
	repo := filepath.Join(t.TempDir(), "repository")
	result, err := Execute(Request{Operation: "initialize", Repository: repo})
	if err != nil {
		t.Fatal(err)
	}
	keys := map[string]string{}
	for _, role := range []string{"targets", "snapshot", "timestamp"} {
		keys[role] = result.Keys[role]
	}
	return Request{Operation: "publish", Repository: repo, BootstrapHash: result.BootstrapHash, Keys: keys}, result
}

func candidate(t *testing.T, version string) Package {
	t.Helper()
	path := filepath.Join(t.TempDir(), version+".zip")
	if err := os.WriteFile(path, []byte("a small signed-package stand-in for metadata tests "+version), 0600); err != nil {
		t.Fatal(err)
	}
	return Package{Path: path, Name: "AudioPilot-" + version + "-win-x64.zip", Identity: Identity{
		Product: "AudioPilot", Channel: "staging", Version: version, Kind: "portable", Architecture: "win-x64", Runtime: "self-contained",
		MinimumHelperVersion: "1.0.0", WindowsPublisherSHA256: strings.Repeat("A", 64), WindowsTrust: "self-signed-staging"}}
}

func TestCatalogRetentionPreservesRecentPausedTargetsAndImmutableHistory(t *testing.T) {
	req, _ := authority(t)
	first := candidate(t, "1.0.1")
	for _, version := range []string{"1.0.1", "1.0.2", "1.0.3", "1.0.4"} {
		req.Packages = []Package{candidate(t, version)}
		if _, err := Execute(req); err != nil {
			t.Fatal(err)
		}
	}
	stamp, _, err := load[metadata.TimestampType](filepath.Join(req.Repository, "metadata", "timestamp.json"))
	if err != nil {
		t.Fatal(err)
	}
	snapshot, _, err := load[metadata.SnapshotType](filepath.Join(req.Repository, "metadata", fmt.Sprintf("%d.snapshot.json", stamp.Signed.Meta["snapshot.json"].Version)))
	if err != nil {
		t.Fatal(err)
	}
	targets, bytes, err := load[metadata.TargetsType](filepath.Join(req.Repository, "metadata", fmt.Sprintf("%d.targets.json", snapshot.Signed.Meta["targets.json"].Version)))
	if err != nil {
		t.Fatal(err)
	}
	if len(targets.Signed.Targets) != 3 || len(bytes) > 131072 {
		t.Fatal("active catalog is not bounded")
	}
	if _, exists := targets.Signed.Targets[first.Name]; exists {
		t.Fatal("oldest target was retained")
	}
	for _, version := range []string{"1.0.2", "1.0.3", "1.0.4"} {
		if _, exists := targets.Signed.Targets["AudioPilot-"+version+"-win-x64.zip"]; !exists {
			t.Fatal("recent paused target was removed")
		}
	}
	data, err := os.ReadFile(first.Path)
	if err != nil {
		t.Fatal(err)
	}
	hash := sha256.Sum256(data)
	if _, err = os.Stat(filepath.Join(req.Repository, "targets", hex.EncodeToString(hash[:])+"."+first.Name)); err != nil {
		t.Fatal("immutable history was deleted:", err)
	}
	req.Packages = []Package{first}
	if _, err = Execute(req); err == nil {
		t.Fatal("retention allowed an old version to be published again")
	}
}

type localFiles string

func (f localFiles) DownloadFile(location string, _ int64, _ time.Duration) ([]byte, error) {
	path := strings.TrimPrefix(location, "https://staging.invalid/")
	if strings.Contains(path, "..") {
		return nil, os.ErrPermission
	}
	data, err := os.ReadFile(filepath.Join(string(f), filepath.FromSlash(path)))
	if os.IsNotExist(err) {
		return nil, &metadata.ErrDownloadHTTP{StatusCode: 404, URL: location}
	}
	return data, err
}

func TestPublicationRenewalAndMaintainedClient(t *testing.T) {
	req, _ := authority(t)
	first := candidate(t, "1.0.1")
	req.Packages = []Package{first}
	result, err := Execute(req)
	if err != nil || result.Version != 1 || result.PublicLiveUpdates || result.Channel != "staging" {
		t.Fatalf("first publish: %+v %v", result, err)
	}
	root, err := os.ReadFile(filepath.Join(req.Repository, "bootstrap.root.json"))
	if err != nil {
		t.Fatal(err)
	}
	cfg, err := config.New("https://staging.invalid/metadata/", root)
	if err != nil {
		t.Fatal(err)
	}
	cfg.Fetcher = localFiles(req.Repository)
	cfg.LocalMetadataDir = t.TempDir()
	cfg.LocalTargetsDir = t.TempDir()
	client, err := updater.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err = client.Refresh(); err != nil {
		t.Fatal(err)
	}
	info, err := client.GetTargetInfo(first.Name)
	if err != nil {
		t.Fatal(err)
	}
	_, bytes, err := client.DownloadTarget(info, filepath.Join(t.TempDir(), "download.zip"), "https://staging.invalid/targets/")
	if err != nil || len(bytes) == 0 {
		t.Fatalf("maintained client target download: %v", err)
	}
	physical := filepath.Join(req.Repository, "targets", hex.EncodeToString(info.Hashes["sha256"])+"."+first.Name)
	if _, err = os.Stat(physical); err != nil {
		t.Fatal(err)
	}
	req.Operation = "renew"
	req.Packages = nil
	result, err = Execute(req)
	if err != nil || result.Version != 2 {
		t.Fatalf("renew: %+v %v", result, err)
	}
	req.Operation = "publish"
	req.Packages = []Package{candidate(t, "1.0.2")}
	result, err = Execute(req)
	if err != nil || result.Version != 3 {
		t.Fatalf("second publish: %+v %v", result, err)
	}
	client, err = updater.New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	if err = client.Refresh(); err != nil {
		t.Fatal(err)
	}
	if len(client.GetTopLevelTargets()) != 2 {
		t.Fatal("older authorized targets disappeared")
	}
	if _, err = os.Stat(filepath.Join(req.Repository, "metadata", "1.snapshot.json")); err != nil {
		t.Fatal("older concurrent-reader metadata removed", err)
	}
	checked, err := Execute(Request{Operation: "check", Repository: req.Repository, BootstrapHash: req.BootstrapHash})
	if err != nil || checked.Version != 3 || len(checked.Files) != 11 || len(checked.Keys) != 0 {
		t.Fatalf("public export verification: %+v %v", checked, err)
	}
	if err = os.WriteFile(physical, []byte("tampered"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err = Execute(Request{Operation: "check", Repository: req.Repository, BootstrapHash: req.BootstrapHash}); err == nil {
		t.Fatal("modified published target was accepted")
	}
}

func TestPublisherRefusesAuthorityAndHistoryFailures(t *testing.T) {
	for _, failure := range []string{"wrong-key", "wrong-root", "root-online", "missing-timestamp", "tampered-snapshot", "downgrade", "same-version", "interrupted-version", "busy"} {
		t.Run(failure, func(t *testing.T) {
			req, initial := authority(t)
			req.Packages = []Package{candidate(t, "1.0.2")}
			if _, err := Execute(req); err != nil {
				t.Fatal(err)
			}
			req.Packages = []Package{candidate(t, "1.0.3")}
			var err error
			switch failure {
			case "wrong-root":
				req.BootstrapHash = strings.Repeat("F", 64)
			case "wrong-key":
				req.Keys["targets"] = initial.Keys["timestamp"]
			case "root-online":
				req.Keys["root"] = initial.Keys["root"]
			case "missing-timestamp":
				err = os.Remove(filepath.Join(req.Repository, "metadata", "timestamp.json"))
			case "tampered-snapshot":
				err = os.WriteFile(filepath.Join(req.Repository, "metadata", "1.snapshot.json"), []byte("{}"), 0600)
			case "downgrade":
				req.Packages = []Package{candidate(t, "1.0.1")}
			case "same-version":
				req.Packages = []Package{candidate(t, "1.0.2")}
			case "busy":
				err = os.WriteFile(filepath.Join(req.Repository, ".publisher.lock"), nil, 0600)
			case "interrupted-version":
				err = os.WriteFile(filepath.Join(req.Repository, "metadata", "2.targets.json"), []byte("interrupted"), 0600)
				if err != nil {
					t.Fatal(err)
				}
				result, e := Execute(req)
				if e != nil || result.Version != 3 {
					t.Fatalf("did not skip interrupted immutable version: %+v %v", result, e)
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if _, err = Execute(req); err == nil {
				t.Fatal("unsafe publication accepted")
			}
		})
	}
}

func TestInterruptedTargetIsReusedOnlyWhenItsBytesMatch(t *testing.T) {
	for _, tampered := range []bool{false, true} {
		t.Run(fmt.Sprint(tampered), func(t *testing.T) {
			req, _ := authority(t)
			pkg := candidate(t, "1.0.1")
			bytes, err := os.ReadFile(pkg.Path)
			if err != nil {
				t.Fatal(err)
			}
			hash := sha256.Sum256(bytes)
			path := filepath.Join(req.Repository, "targets", hex.EncodeToString(hash[:])+"."+pkg.Name)
			if tampered {
				bytes = []byte("incomplete copy")
			}
			if err = os.WriteFile(path, bytes, 0600); err != nil {
				t.Fatal(err)
			}
			req.Packages = []Package{pkg}
			_, err = Execute(req)
			if tampered && err == nil {
				t.Fatal("reused damaged immutable target")
			}
			if !tampered && err != nil {
				t.Fatal(err)
			}
		})
	}
}

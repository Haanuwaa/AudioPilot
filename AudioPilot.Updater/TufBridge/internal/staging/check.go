package staging

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"time"

	"github.com/theupdateframework/go-tuf/v2/metadata"
	"github.com/theupdateframework/go-tuf/v2/metadata/config"
	"github.com/theupdateframework/go-tuf/v2/metadata/updater"
)

type repositoryFiles string

func (f repositoryFiles) DownloadFile(location string, maximum int64, _ time.Duration) ([]byte, error) {
	u, err := url.Parse(location)
	if err != nil || u.Scheme != "https" || u.Host != "staging.invalid" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || !strings.HasPrefix(u.Path, "/metadata/") {
		return nil, errors.New("unexpected verification URL")
	}
	name := strings.TrimPrefix(u.Path, "/metadata/")
	if strings.ContainsAny(name, "/\\%") || strings.Contains(name, "..") || !targetPattern.MatchString(name) {
		return nil, errors.New("unsafe metadata path")
	}
	bytes, err := read(filepath.Join(string(f), "metadata", name), maximum)
	if os.IsNotExist(err) {
		return nil, &metadata.ErrDownloadHTTP{StatusCode: 404, URL: location}
	}
	return bytes, err
}

func check(req Request) (Result, error) {
	roots, err := trustedRoots(req)
	if err != nil {
		return Result{}, err
	}
	root, err := read(filepath.Join(req.Repository, "bootstrap.root.json"), 65536)
	if err != nil {
		return Result{}, err
	}
	hash := sha256.Sum256(root)
	if !digestPattern.MatchString(req.BootstrapHash) || !strings.EqualFold(hex.EncodeToString(hash[:]), req.BootstrapHash) {
		return Result{}, errors.New("feed root differs from independent pin")
	}
	versionedRoot, err := read(filepath.Join(req.Repository, "metadata", "1.root.json"), 65536)
	if err != nil || !slices.Equal(root, versionedRoot) {
		return Result{}, errors.New("versioned staging root differs from bootstrap")
	}
	cfg, err := config.New("https://staging.invalid/metadata/", root)
	if err != nil {
		return Result{}, err
	}
	cfg.Fetcher = repositoryFiles(req.Repository)
	cfg.DisableLocalCache = true
	cfg.RootMaxLength = 65536
	cfg.TimestampMaxLength = 16384
	cfg.SnapshotMaxLength = 131072
	cfg.TargetsMaxLength = 131072
	client, err := updater.New(cfg)
	if err != nil {
		return Result{}, err
	}
	if err = client.Refresh(); err != nil {
		return Result{}, err
	}
	files := []string{"bootstrap.root.json", "metadata/1.root.json", "metadata/timestamp.json"}
	artifacts := []PublicArtifact{}
	stamp, _, err := load[metadata.TimestampType](filepath.Join(req.Repository, "metadata", "timestamp.json"))
	if err != nil {
		return Result{}, err
	}
	for name, target := range client.GetTopLevelTargets() {
		var id Identity
		if !targetPattern.MatchString(name) || strings.Contains(name, "..") || target.Custom == nil || json.Unmarshal(*target.Custom, &id) != nil ||
			id.Product != "AudioPilot" || id.Channel != "staging" || !versionPattern.MatchString(id.Version) || !digestPattern.MatchString(id.WindowsPublisherSHA256) ||
			!slices.Contains([]string{"self-signed-staging", "publicly-trusted"}, id.WindowsTrust) || len(target.Hashes["sha256"]) != 32 || target.Length <= 0 || target.Length > 1024*1024*1024 {
			return Result{}, errors.New("invalid staging target")
		}
		relative := "targets/" + hex.EncodeToString(target.Hashes["sha256"]) + "." + name
		artifacts = append(artifacts, PublicArtifact{filepath.Base(relative), target.Length, hex.EncodeToString(target.Hashes["sha256"])})
		if req.MetadataOnly {
			continue
		}
		path := filepath.Join(req.Repository, filepath.FromSlash(relative))
		if err = cleanPath(path); err != nil {
			return Result{}, err
		}
		file, e := os.Open(path)
		if e != nil {
			return Result{}, e
		}
		hasher := sha256.New()
		length, e := io.Copy(hasher, io.LimitReader(file, target.Length+1))
		e = errors.Join(e, file.Close())
		if e != nil || length != target.Length || !slices.Equal(hasher.Sum(nil), target.Hashes["sha256"]) {
			return Result{}, errors.New("staging target length or digest mismatch")
		}
		files = append(files, relative)
	}
	entries, err := os.ReadDir(filepath.Join(req.Repository, "metadata"))
	if err != nil {
		return Result{}, err
	}
	for _, entry := range entries {
		if entry.Name() == "timestamp.json" || entry.Name() == "1.root.json" {
			continue
		}
		var version int64
		var role string
		if _, e := fmt.Sscanf(entry.Name(), "%d.%s", &version, &role); e != nil || version <= 0 || version > 1000000 || !slices.Contains([]string{"root.json", "targets.json", "snapshot.json"}, role) || entry.Name() != fmt.Sprintf("%d.%s", version, role) {
			return Result{}, errors.New("unexpected metadata export entry")
		}
		if role == "root.json" {
			if version > int64(len(roots)) {
				return Result{}, errors.New("root chain has a gap or exceeded its bound")
			}
			files = append(files, "metadata/"+entry.Name())
			continue
		}
		path := filepath.Join(req.Repository, "metadata", entry.Name())
		if role == "targets.json" {
			old, _, e := load[metadata.TargetsType](path)
			if e != nil {
				return Result{}, e
			}
			if old.Signed.Version != version {
				return Result{}, errors.New("historical targets version mismatch")
			}
			if e = verifyHistorical(roots, "targets", old); e != nil {
				return Result{}, e
			}
		} else {
			old, _, e := load[metadata.SnapshotType](path)
			if e != nil {
				return Result{}, e
			}
			if old.Signed.Version != version {
				return Result{}, errors.New("historical snapshot version mismatch")
			}
			if e = verifyHistorical(roots, "snapshot", old); e != nil {
				return Result{}, e
			}
		}
		files = append(files, "metadata/"+entry.Name())
	}
	slices.Sort(files)
	slices.SortFunc(artifacts, func(a, b PublicArtifact) int { return strings.Compare(a.Name, b.Name) })
	latest := roots[len(roots)-1]
	snapshot, _, err := load[metadata.SnapshotType](filepath.Join(req.Repository, "metadata", fmt.Sprintf("%d.snapshot.json", stamp.Signed.Meta["snapshot.json"].Version)))
	if err != nil {
		return Result{}, err
	}
	targets, _, err := load[metadata.TargetsType](filepath.Join(req.Repository, "metadata", fmt.Sprintf("%d.targets.json", snapshot.Signed.Meta["targets.json"].Version)))
	if err != nil {
		return Result{}, err
	}
	return Result{BootstrapHash: strings.ToUpper(req.BootstrapHash), Version: stamp.Signed.Version, Channel: "staging", Files: files,
		RootVersion: latest.Signed.Version, RootThreshold: latest.Signed.Roles["root"].Threshold,
		MetadataOnly: req.MetadataOnly,
		Artifacts:    artifacts,
		Expires:      map[string]time.Time{"root": latest.Signed.Expires, "targets": targets.Signed.Expires, "snapshot": snapshot.Signed.Expires, "timestamp": stamp.Signed.Expires}}, nil
}

// Historical immutable versions can predate revocation. The maintained client independently verifies the current catalog against the latest root.
func verifyHistorical[T metadata.TargetsType | metadata.SnapshotType](roots []*metadata.Metadata[metadata.RootType], role string, md *metadata.Metadata[T]) error {
	for _, root := range roots {
		if root.VerifyDelegate(role, md) == nil {
			return nil
		}
	}
	return errors.New("historical metadata has no authorized signer")
}

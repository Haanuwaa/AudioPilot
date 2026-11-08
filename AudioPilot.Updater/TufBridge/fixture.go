package main

import (
	"crypto"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/sigstore/sigstore/pkg/signature"
	"github.com/theupdateframework/go-tuf/v2/metadata"
)

type repositoryFixture struct {
	root      *metadata.Metadata[metadata.RootType]
	targets   *metadata.Metadata[metadata.TargetsType]
	snapshot  *metadata.Metadata[metadata.SnapshotType]
	timestamp *metadata.Metadata[metadata.TimestampType]
	keys      map[string]ed25519.PrivateKey
}

func newFixture() (*repositoryFixture, error) {
	now := time.Now().UTC()
	f := &repositoryFixture{root: metadata.Root(now.Add(48 * time.Hour)), targets: metadata.Targets(now.Add(24 * time.Hour)), snapshot: metadata.Snapshot(now.Add(24 * time.Hour)), timestamp: metadata.Timestamp(now.Add(12 * time.Hour)), keys: map[string]ed25519.PrivateKey{}}
	for _, role := range []string{"root", "targets", "snapshot", "timestamp"} {
		_, key, err := ed25519.GenerateKey(nil)
		if err != nil {
			return nil, err
		}
		f.keys[role] = key
		pub, err := metadata.KeyFromPublicKey(key.Public())
		if err != nil {
			return nil, err
		}
		if err = f.root.Signed.AddKey(pub, role); err != nil {
			return nil, err
		}
	}
	return f, nil
}
func (f *repositoryFixture) dispose() {
	for _, key := range f.keys {
		clear(key)
	}
}
func (f *repositoryFixture) write(repo string) error {
	for _, role := range []string{"root", "targets", "snapshot", "timestamp"} {
		signer, err := signature.LoadSigner(f.keys[role], crypto.Hash(0))
		if err != nil {
			return err
		}
		switch role {
		case "root":
			f.root.Signatures = nil
			_, err = f.root.Sign(signer)
		case "targets":
			f.targets.Signatures = nil
			_, err = f.targets.Sign(signer)
		case "snapshot":
			data, e := f.targets.ToBytes(true)
			if e != nil {
				return e
			}
			sum := sha256.Sum256(data)
			f.snapshot.Signed.Meta["targets.json"] = &metadata.MetaFiles{Version: f.targets.Signed.Version, Length: int64(len(data)), Hashes: metadata.Hashes{"sha256": sum[:]}}
			f.snapshot.Signatures = nil
			_, err = f.snapshot.Sign(signer)
		case "timestamp":
			data, e := f.snapshot.ToBytes(true)
			if e != nil {
				return e
			}
			sum := sha256.Sum256(data)
			f.timestamp.Signed.Meta["snapshot.json"] = &metadata.MetaFiles{Version: f.snapshot.Signed.Version, Length: int64(len(data)), Hashes: metadata.Hashes{"sha256": sum[:]}}
			f.timestamp.Signatures = nil
			_, err = f.timestamp.Sign(signer)
		}
		if err != nil {
			return err
		}
	}
	dir := filepath.Join(repo, "metadata")
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	if err := f.root.ToFile(filepath.Join(dir, fmt.Sprintf("%d.root.json", f.root.Signed.Version)), true); err != nil {
		return err
	}
	if _, err := os.Stat(filepath.Join(repo, "bootstrap.root.json")); os.IsNotExist(err) {
		if err = f.root.ToFile(filepath.Join(repo, "bootstrap.root.json"), true); err != nil {
			return err
		}
	}
	if err := f.targets.ToFile(filepath.Join(dir, fmt.Sprintf("%d.targets.json", f.targets.Signed.Version)), true); err != nil {
		return err
	}
	if err := f.snapshot.ToFile(filepath.Join(dir, fmt.Sprintf("%d.snapshot.json", f.snapshot.Signed.Version)), true); err != nil {
		return err
	}
	return f.timestamp.ToFile(filepath.Join(dir, "timestamp.json"), true)
}
func createFixture(req request) (response, error) {
	var empty response
	repo, err := safePath(req.Repository)
	if err != nil {
		return empty, err
	}
	if _, err = os.Stat(repo); !os.IsNotExist(err) {
		return empty, errors.New("choose a new local TUF repository")
	}
	path, err := safePath(req.Package)
	if err != nil {
		return empty, err
	}
	name := filepath.Base(path)
	if !validTarget(name) || req.Kind != "msi" && req.Kind != "portable" {
		return empty, errors.New("invalid local fixture package")
	}
	f, err := newFixture()
	if err != nil {
		return empty, err
	}
	defer f.dispose()
	input, err := os.Open(path)
	if err != nil {
		return empty, err
	}
	defer input.Close()
	stat, err := input.Stat()
	if err != nil {
		return empty, err
	}
	if !stat.Mode().IsRegular() || stat.Size() <= 0 || stat.Size() > 1024*1024*1024 {
		return empty, errors.New("invalid fixture package length")
	}
	hasher := sha256.New()
	if _, err = io.Copy(hasher, input); err != nil {
		return empty, err
	}
	custom := targetIdentity{Product: "AudioPilot", Channel: "local-test", Version: "1.0.1", Kind: req.Kind, Architecture: "win-x64", Runtime: "self-contained", MinimumHelperVersion: "1.0.0"}
	encoded, err := json.Marshal(custom)
	if err != nil {
		return empty, err
	}
	raw := json.RawMessage(encoded)
	target := metadata.TargetFile()
	target.Length = stat.Size()
	target.Hashes = metadata.Hashes{"sha256": hasher.Sum(nil)}
	target.Custom = &raw
	f.targets.Signed.Targets[name] = target
	if err = f.write(repo); err != nil {
		return empty, err
	}
	root, err := os.ReadFile(filepath.Join(repo, "bootstrap.root.json"))
	if err != nil {
		return empty, err
	}
	pin := sha256.Sum256(root)
	return response{BootstrapHash: strings.ToUpper(hex.EncodeToString(pin[:])), Target: name, Length: target.Length, SHA256: strings.ToUpper(hex.EncodeToString(target.Hashes["sha256"])), Custom: &custom}, nil
}

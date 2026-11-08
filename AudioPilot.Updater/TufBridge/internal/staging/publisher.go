// Package staging publishes the deliberately separate, opt-in staging channel.
// go-tuf owns canonical serialization, signatures and metadata verification.
package staging

import (
	"crypto"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/sigstore/sigstore/pkg/signature"
	"github.com/theupdateframework/go-tuf/v2/metadata"
)

type Request struct {
	Operation          string            `json:"operation"`
	Repository         string            `json:"repository"`
	BootstrapHash      string            `json:"bootstrapHash"`
	Keys               map[string]string `json:"keys"`
	Packages           []Package         `json:"packages"`
	Destination        string            `json:"destination,omitempty"`
	RootSigners        []string          `json:"rootSigners,omitempty"`
	RootThreshold      int               `json:"rootThreshold,omitempty"`
	Backup             string            `json:"backup,omitempty"`
	Passphrase         string            `json:"passphrase,omitempty"`
	MinimumVersion     int64             `json:"minimumVersion,omitempty"`
	MetadataOnly       bool              `json:"metadataOnly,omitempty"`
	MinimumRootVersion int64             `json:"minimumRootVersion,omitempty"`
}

type Package struct {
	Path     string   `json:"path"`
	Name     string   `json:"name"`
	Identity Identity `json:"identity"`
}

type Identity struct {
	Product                string `json:"product"`
	Channel                string `json:"channel"`
	Version                string `json:"version"`
	Kind                   string `json:"kind"`
	Architecture           string `json:"architecture"`
	Runtime                string `json:"runtime"`
	MinimumHelperVersion   string `json:"minimumHelperVersion"`
	WindowsPublisherSHA256 string `json:"windowsPublisherSha256"`
	WindowsTrust           string `json:"windowsTrust"`
}

type Result struct {
	BootstrapHash     string               `json:"bootstrapHash"`
	Keys              map[string]string    `json:"keys,omitempty"`
	Version           int64                `json:"version"`
	Channel           string               `json:"channel"`
	PublicLiveUpdates bool                 `json:"publicLiveUpdates"`
	Files             []string             `json:"files,omitempty"`
	RootVersion       int64                `json:"rootVersion"`
	RootThreshold     int                  `json:"rootThreshold"`
	Expires           map[string]time.Time `json:"expires,omitempty"`
	MetadataOnly      bool                 `json:"metadataOnly,omitempty"`
	Artifacts         []PublicArtifact     `json:"artifacts,omitempty"`
}

type PublicArtifact struct {
	Name   string `json:"name"`
	Length int64  `json:"length"`
	SHA256 string `json:"sha256"`
}

var versionPattern = regexp.MustCompile(`^(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})$`)
var targetPattern = regexp.MustCompile(`^[A-Za-z0-9][A-Za-z0-9._-]{0,159}$`)
var digestPattern = regexp.MustCompile(`^[a-fA-F0-9]{64}$`)
var roleLabelPattern = regexp.MustCompile(`^(root(-[1-3])?|targets|snapshot|timestamp)$`)

func Execute(req Request) (Result, error) {
	if req.MinimumRootVersion < 0 || req.MinimumRootVersion > 32 || req.MinimumVersion < 0 || req.MinimumVersion > 1000000 {
		return Result{}, errors.New("invalid recovery floor")
	}
	if (req.Operation != "backup" && req.Operation != "restore" && (req.Backup != "" || req.Passphrase != "" || req.MinimumVersion != 0)) ||
		(req.Operation != "rotate" && (req.Destination != "" || len(req.RootSigners) != 0 || req.RootThreshold != 0)) ||
		(req.MetadataOnly && req.Operation != "check") {
		return Result{}, errors.New("options do not belong to this operation")
	}
	if !filepath.IsAbs(req.Repository) || cleanPath(req.Repository) != nil {
		return Result{}, errors.New("publisher needs an absolute, unredirected repository path")
	}
	if req.Operation == "initialize" {
		if len(req.Keys) != 0 || len(req.Packages) != 0 || req.BootstrapHash != "" {
			return Result{}, errors.New("initialization cannot accept existing authority material")
		}
		return initialize(req.Repository)
	}
	if req.Operation == "check" {
		if len(req.Keys) != 0 || len(req.Packages) != 0 {
			return Result{}, errors.New("feed verification does not accept keys or packages")
		}
		return check(req)
	}
	if req.Operation == "backup" || req.Operation == "restore" {
		return custody(req)
	}
	if req.Operation == "rotate" {
		return rotate(req)
	}
	if req.Operation != "publish" && req.Operation != "renew" {
		return Result{}, errors.New("unsupported staging publisher operation")
	}
	lock, err := os.OpenFile(filepath.Join(req.Repository, ".publisher.lock"), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return Result{}, fmt.Errorf("publisher already active or interrupted; inspect its lock: %w", err)
	}
	defer func() { lock.Close(); os.Remove(lock.Name()) }()
	return publish(req, time.Now().UTC())
}

func cleanPath(path string) error {
	for current := filepath.Clean(path); ; current = filepath.Dir(current) {
		info, err := os.Lstat(current)
		if err == nil {
			if info.Mode()&os.ModeSymlink != 0 {
				return errors.New("redirected path")
			}
			real, err := filepath.EvalSymlinks(current)
			if err != nil || !strings.EqualFold(real, current) {
				return errors.New("redirected path")
			}
		} else if !os.IsNotExist(err) {
			return err
		}
		if filepath.Dir(current) == current {
			return nil
		}
	}
}

func initialize(repo string) (Result, error) {
	if _, err := os.Stat(repo); !os.IsNotExist(err) {
		return Result{}, errors.New("choose a new staging repository")
	}
	root := metadata.Root(time.Now().UTC().Add(365 * 24 * time.Hour))
	keys := map[string]string{}
	var rootKey ed25519.PrivateKey
	for _, role := range []string{"root", "targets", "snapshot", "timestamp"} {
		_, key, err := ed25519.GenerateKey(nil)
		if err != nil {
			return Result{}, err
		}
		keys[role] = base64.StdEncoding.EncodeToString(key.Seed())
		pub, err := metadata.KeyFromPublicKey(key.Public())
		if err != nil {
			return Result{}, err
		}
		if err = root.Signed.AddKey(pub, role); err != nil {
			return Result{}, err
		}
		if role == "root" {
			rootKey = key
		} else {
			clear(key)
		}
	}
	defer clear(rootKey)
	if err := sign(root, rootKey); err != nil {
		return Result{}, err
	}
	bytes, err := root.ToBytes(true)
	if err != nil {
		return Result{}, err
	}
	if err = os.MkdirAll(filepath.Join(repo, "metadata"), 0700); err != nil {
		return Result{}, err
	}
	if err = os.Mkdir(filepath.Join(repo, "targets"), 0700); err != nil {
		return Result{}, err
	}
	for _, path := range []string{filepath.Join(repo, "bootstrap.root.json"), filepath.Join(repo, "metadata", "1.root.json")} {
		if err = immutable(path, bytes); err != nil {
			return Result{}, err
		}
	}
	hash := sha256.Sum256(bytes)
	return Result{BootstrapHash: strings.ToUpper(hex.EncodeToString(hash[:])), Keys: keys, Channel: "staging", RootVersion: 1, RootThreshold: 1}, nil
}

func sign[T metadata.RootType | metadata.TargetsType | metadata.SnapshotType | metadata.TimestampType](md *metadata.Metadata[T], key ed25519.PrivateKey) error {
	signer, err := signature.LoadSigner(key, crypto.Hash(0))
	if err != nil {
		return err
	}
	md.Signatures = nil
	_, err = md.Sign(signer)
	return err
}

func read(path string, limit int64) ([]byte, error) {
	if err := cleanPath(path); err != nil {
		return nil, err
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	stat, err := file.Stat()
	if err != nil || !stat.Mode().IsRegular() || stat.Size() > limit {
		return nil, errors.New("invalid publisher input length or type")
	}
	bytes, err := io.ReadAll(io.LimitReader(file, limit+1))
	if int64(len(bytes)) > limit {
		return nil, errors.New("publisher input exceeded its bound")
	}
	return bytes, err
}

func load[T metadata.RootType | metadata.TargetsType | metadata.SnapshotType | metadata.TimestampType](path string) (*metadata.Metadata[T], []byte, error) {
	bytes, err := read(path, 131072)
	if err != nil {
		return nil, nil, err
	}
	md, err := new(metadata.Metadata[T]).FromBytes(bytes)
	return md, bytes, err
}

func publish(req Request, now time.Time) (Result, error) {
	return publishWithPrevious(req, now, nil)
}

func publishWithPrevious(req Request, now time.Time, previousRoot *metadata.Metadata[metadata.RootType]) (Result, error) {
	roots, err := trustedRoots(req)
	if err != nil {
		return Result{}, err
	}
	root := roots[len(roots)-1]
	if !root.Signed.Expires.After(now) || !root.Signed.ConsistentSnapshot {
		return Result{}, errors.New("expired or incompatible staging root")
	}
	if previousRoot == nil {
		previousRoot = root
	}
	keys := map[string]ed25519.PrivateKey{}
	defer func() {
		for _, key := range keys {
			clear(key)
		}
	}()
	roles := []string{"snapshot", "timestamp"}
	if req.Operation == "publish" || previousRoot != root {
		roles = append(roles, "targets")
	}
	// Explicit manual renewal may also renew targets. Unattended renewal cannot authorize a catalog.
	if req.Operation == "renew" && len(req.Keys) == 3 && previousRoot == root {
		roles = append(roles, "targets")
	}
	if len(req.Keys) != len(roles) {
		return Result{}, errors.New("supply only the required role keys; root stays offline")
	}
	for _, role := range roles {
		seed, err := base64.StdEncoding.DecodeString(req.Keys[role])
		if err != nil || len(seed) != ed25519.SeedSize {
			return Result{}, errors.New("invalid online signing key")
		}
		key := ed25519.NewKeyFromSeed(seed)
		clear(seed)
		keys[role] = key
		pub, err := metadata.KeyFromPublicKey(key.Public())
		if err != nil {
			return Result{}, err
		}
		keyID, err := pub.ID()
		if err != nil {
			return Result{}, err
		}
		if root.Signed.Roles[role] == nil || !slices.Contains(root.Signed.Roles[role].KeyIDs, keyID) {
			return Result{}, errors.New("online key does not belong to pinned authority")
		}
	}
	mdDir := filepath.Join(req.Repository, "metadata")
	if err = cleanPath(mdDir); err != nil {
		return Result{}, err
	}
	targets := metadata.Targets(now.Add(30 * 24 * time.Hour))
	next := int64(1)
	timestampPath := filepath.Join(mdDir, "timestamp.json")
	if _, e := os.Stat(timestampPath); e == nil {
		previous, _, e := load[metadata.TimestampType](timestampPath)
		if e != nil {
			return Result{}, e
		}
		if e = previousRoot.VerifyDelegate("timestamp", previous); e != nil {
			return Result{}, e
		}
		info := previous.Signed.Meta["snapshot.json"]
		if info == nil {
			return Result{}, errors.New("missing snapshot reference")
		}
		snapshot, bytes, e := load[metadata.SnapshotType](filepath.Join(mdDir, fmt.Sprintf("%d.snapshot.json", info.Version)))
		if e != nil {
			return Result{}, e
		}
		if snapshot.Signed.Version != info.Version {
			return Result{}, errors.New("snapshot version mismatch")
		}
		if e = info.VerifyLengthHashes(bytes); e != nil {
			return Result{}, e
		}
		if e = previousRoot.VerifyDelegate("snapshot", snapshot); e != nil {
			return Result{}, e
		}
		info = snapshot.Signed.Meta["targets.json"]
		if info == nil {
			return Result{}, errors.New("missing targets reference")
		}
		oldTargets, bytes, e := load[metadata.TargetsType](filepath.Join(mdDir, fmt.Sprintf("%d.targets.json", info.Version)))
		if e != nil {
			return Result{}, e
		}
		if oldTargets.Signed.Version != info.Version {
			return Result{}, errors.New("targets version mismatch")
		}
		if e = info.VerifyLengthHashes(bytes); e != nil {
			return Result{}, e
		}
		if e = previousRoot.VerifyDelegate("targets", oldTargets); e != nil {
			return Result{}, e
		}
		targets.Signed.Targets = oldTargets.Signed.Targets
		if keys["targets"] == nil {
			targets = oldTargets
		}
		next = previous.Signed.Version + 1
	} else if !os.IsNotExist(e) {
		return Result{}, e
	} else if _, e = os.Stat(filepath.Join(req.Repository, "published.anchor")); !os.IsNotExist(e) {
		return Result{}, errors.New("published timestamp missing; refusing to reset authority history")
	}
	if req.Operation == "renew" && (len(req.Packages) != 0 || len(targets.Signed.Targets) == 0) {
		return Result{}, errors.New("renew needs existing targets and no new packages")
	}
	if req.Operation == "publish" && (len(req.Packages) == 0 || len(req.Packages) > 9) {
		return Result{}, errors.New("select one to nine explicit staging packages")
	}
	// An interrupted pre-commit write may leave immutable versions. Never reuse them.
	entries, err := os.ReadDir(mdDir)
	if err != nil {
		return Result{}, err
	}
	for _, entry := range entries {
		parts := strings.Split(entry.Name(), ".")
		if len(parts) == 3 && (parts[1] == "snapshot" || parts[1] == "targets") && parts[2] == "json" {
			if v, e := strconv.ParseInt(parts[0], 10, 64); e == nil && v >= next {
				next = v + 1
			}
		}
	}
	if next <= 0 || next > 1000000 {
		return Result{}, errors.New("staging metadata version outside its bound")
	}
	for _, pkg := range req.Packages {
		if err = addPackage(req.Repository, targets, pkg); err != nil {
			return Result{}, err
		}
	}
	if keys["targets"] != nil {
		targets.Signed.Version = next
		if err = sign(targets, keys["targets"]); err != nil {
			return Result{}, err
		}
	} else if !targets.Signed.Expires.After(now.Add(24 * time.Hour)) {
		return Result{}, errors.New("targets approval expires within 24 hours; manual targets renewal required")
	}
	targetBytes, err := targets.ToBytes(true)
	if err != nil {
		return Result{}, err
	}
	if len(targetBytes) > 131072 {
		return Result{}, errors.New("staging targets metadata exceeded the client bound")
	}
	snapshot := metadata.Snapshot(now.Add(7 * 24 * time.Hour))
	snapshot.Signed.Version = next
	snapshot.Signed.Meta["targets.json"] = reference(targets.Signed.Version, targetBytes)
	if err = sign(snapshot, keys["snapshot"]); err != nil {
		return Result{}, err
	}
	snapshotBytes, err := snapshot.ToBytes(true)
	if err != nil {
		return Result{}, err
	}
	timestamp := metadata.Timestamp(now.Add(24 * time.Hour))
	timestamp.Signed.Version = next
	timestamp.Signed.Meta["snapshot.json"] = reference(next, snapshotBytes)
	if err = sign(timestamp, keys["timestamp"]); err != nil {
		return Result{}, err
	}
	timestampBytes, err := timestamp.ToBytes(true)
	if err != nil {
		return Result{}, err
	}
	if err = immutable(filepath.Join(mdDir, fmt.Sprintf("%d.targets.json", targets.Signed.Version)), targetBytes); err != nil {
		return Result{}, err
	}
	if err = immutable(filepath.Join(mdDir, fmt.Sprintf("%d.snapshot.json", next)), snapshotBytes); err != nil {
		return Result{}, err
	}
	if err = immutable(filepath.Join(req.Repository, "published.anchor"), []byte(strings.ToUpper(req.BootstrapHash))); err != nil {
		return Result{}, err
	}
	if err = replace(timestampPath, timestampBytes); err != nil {
		return Result{}, err
	}
	return Result{BootstrapHash: strings.ToUpper(req.BootstrapHash), Version: next, Channel: "staging", RootVersion: root.Signed.Version, RootThreshold: root.Signed.Roles["root"].Threshold}, nil
}

func reference(version int64, bytes []byte) *metadata.MetaFiles {
	hash := sha256.Sum256(bytes)
	return &metadata.MetaFiles{Version: version, Length: int64(len(bytes)), Hashes: metadata.Hashes{"sha256": hash[:]}}
}

func addPackage(repo string, targets *metadata.Metadata[metadata.TargetsType], pkg Package) error {
	id := pkg.Identity
	extension := ".zip"
	if id.Kind == "msi" {
		extension = ".msi"
	}
	if !targetPattern.MatchString(pkg.Name) || strings.Contains(pkg.Name, "..") || !strings.HasSuffix(pkg.Name, extension) ||
		id.Product != "AudioPilot" || id.Channel != "staging" || !versionPattern.MatchString(id.Version) || !versionPattern.MatchString(id.MinimumHelperVersion) ||
		!slices.Contains([]string{"msi", "portable"}, id.Kind) || !slices.Contains([]string{"win-x64", "win-x86", "win-arm64"}, id.Architecture) ||
		!slices.Contains([]string{"self-contained", "framework-dependent"}, id.Runtime) || !digestPattern.MatchString(id.WindowsPublisherSHA256) ||
		!slices.Contains([]string{"self-signed-staging", "publicly-trusted"}, id.WindowsTrust) || !filepath.IsAbs(pkg.Path) {
		return errors.New("invalid staging target identity")
	}
	if err := cleanPath(pkg.Path); err != nil {
		return err
	}
	input, err := os.Open(pkg.Path)
	if err != nil {
		return err
	}
	defer input.Close()
	stat, err := input.Stat()
	if err != nil || !stat.Mode().IsRegular() || stat.Size() <= 0 || stat.Size() > 1024*1024*1024 {
		return errors.New("invalid package type or length")
	}
	target := metadata.TargetFile()
	target.Length = stat.Size()
	hasher := sha256.New()
	if _, err = io.Copy(hasher, io.LimitReader(input, target.Length+1)); err != nil {
		return err
	}
	target.Hashes = metadata.Hashes{"sha256": hasher.Sum(nil)}
	custom, err := json.Marshal(id)
	if err != nil {
		return err
	}
	raw := json.RawMessage(custom)
	target.Custom = &raw
	for name, old := range targets.Signed.Targets {
		var oldID Identity
		if old.Custom == nil || json.Unmarshal(*old.Custom, &oldID) != nil || !versionPattern.MatchString(oldID.Version) {
			return errors.New("existing target identity is missing")
		}
		if name == pkg.Name {
			return errors.New("target names are immutable; publish a new version")
		}
		if oldID.Kind == id.Kind && oldID.Architecture == id.Architecture && oldID.Runtime == id.Runtime && compareVersion(id.Version, oldID.Version) <= 0 {
			return errors.New("staging package version must advance for its deployment variant")
		}
	}
	targetDirectory := filepath.Join(repo, "targets")
	if err = cleanPath(targetDirectory); err != nil {
		return err
	}
	output := filepath.Join(targetDirectory, hex.EncodeToString(target.Hashes["sha256"])+"."+pkg.Name)
	if _, err = input.Seek(0, io.SeekStart); err != nil {
		return err
	}
	temp, err := os.CreateTemp(targetDirectory, ".package-*")
	if err != nil {
		return err
	}
	defer os.Remove(temp.Name())
	verification := sha256.New()
	count, err := io.Copy(io.MultiWriter(temp, verification), io.LimitReader(input, target.Length+1))
	if err != nil {
		temp.Close()
		return err
	}
	if err = temp.Sync(); err != nil {
		temp.Close()
		return err
	}
	if err = temp.Close(); err != nil {
		return err
	}
	if count != target.Length || !slices.Equal(verification.Sum(nil), target.Hashes["sha256"]) {
		return errors.New("package changed during publication")
	}
	if _, e := os.Stat(output); e == nil {
		if err = cleanPath(output); err != nil {
			return err
		}
		existing, e := os.Open(output)
		if e != nil {
			return e
		}
		h := sha256.New()
		length, e := io.Copy(h, io.LimitReader(existing, target.Length+1))
		e = errors.Join(e, existing.Close())
		if e != nil || length != target.Length || !slices.Equal(h.Sum(nil), target.Hashes["sha256"]) {
			return errors.New("interrupted immutable target differs from approved package")
		}
	} else if !os.IsNotExist(e) {
		return e
	} else if err = os.Rename(temp.Name(), output); err != nil {
		return err
	}
	targets.Signed.Targets[pkg.Name] = target
	return nil
}

func compareVersion(a, b string) int {
	for i, part := range strings.Split(a, ".") {
		x, _ := strconv.Atoi(part)
		y, _ := strconv.Atoi(strings.Split(b, ".")[i])
		if x < y {
			return -1
		}
		if x > y {
			return 1
		}
	}
	return 0
}

func immutable(path string, bytes []byte) error {
	if err := cleanPath(path); err != nil {
		return err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0644)
	if os.IsExist(err) {
		old, e := read(path, 131072)
		if e == nil && string(old) == string(bytes) {
			return nil
		}
		return errors.New("immutable metadata collision")
	}
	if err != nil {
		return err
	}
	_, err = file.Write(bytes)
	if err == nil {
		err = file.Sync()
	}
	return errors.Join(err, file.Close())
}

func replace(path string, bytes []byte) error {
	if err := cleanPath(path); err != nil {
		return err
	}
	file, err := os.CreateTemp(filepath.Dir(path), ".timestamp-*")
	if err != nil {
		return err
	}
	defer os.Remove(file.Name())
	_, err = file.Write(bytes)
	if err == nil {
		err = file.Sync()
	}
	err = errors.Join(err, file.Close())
	if err != nil {
		return err
	}
	return os.Rename(file.Name(), path)
}

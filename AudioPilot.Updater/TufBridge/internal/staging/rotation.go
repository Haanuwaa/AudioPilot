package staging

import (
	"crypto"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"time"

	"github.com/sigstore/sigstore/pkg/signature"
	"github.com/theupdateframework/go-tuf/v2/metadata"
)

// The original pin remains authoritative across every consecutive, dual-signed root.
func trustedRoots(req Request) ([]*metadata.Metadata[metadata.RootType], error) {
	first, bytes, err := load[metadata.RootType](filepath.Join(req.Repository, "bootstrap.root.json"))
	if err != nil {
		return nil, err
	}
	hash := sha256.Sum256(bytes)
	if !digestPattern.MatchString(req.BootstrapHash) || !strings.EqualFold(hex.EncodeToString(hash[:]), req.BootstrapHash) || first.Signed.Version != 1 {
		return nil, errors.New("staging authority root pin mismatch")
	}
	versioned, err := read(filepath.Join(req.Repository, "metadata", "1.root.json"), 65536)
	if err != nil || !slices.Equal(bytes, versioned) {
		return nil, errors.New("bootstrap root copy differs")
	}
	if err = first.VerifyDelegate("root", first); err != nil {
		return nil, err
	}
	roots := []*metadata.Metadata[metadata.RootType]{first}
	for version := int64(2); version <= 32; version++ {
		next, _, err := load[metadata.RootType](filepath.Join(req.Repository, "metadata", fmt.Sprintf("%d.root.json", version)))
		if os.IsNotExist(err) {
			break
		}
		if err != nil {
			return nil, err
		}
		if next.Signed.Version != version {
			return nil, errors.New("nonconsecutive root version")
		}
		if err = roots[len(roots)-1].VerifyDelegate("root", next); err != nil {
			return nil, err
		}
		if err = next.VerifyDelegate("root", next); err != nil {
			return nil, err
		}
		roots = append(roots, next)
	}
	for _, root := range roots {
		if err = requireChannel(root, req); err != nil {
			return nil, err
		}
		ids := map[string]bool{}
		if !root.Signed.ConsistentSnapshot {
			return nil, errors.New("consistent snapshots required")
		}
		for _, role := range []string{"root", "targets", "snapshot", "timestamp"} {
			policy := root.Signed.Roles[role]
			if policy == nil || policy.Threshold < 1 || policy.Threshold > len(policy.KeyIDs) || len(policy.KeyIDs) > 5 || (role != "root" && (policy.Threshold != 1 || len(policy.KeyIDs) != 1)) {
				return nil, errors.New("unsupported role threshold")
			}
			for _, id := range policy.KeyIDs {
				if ids[id] {
					return nil, errors.New("roles must use distinct keys")
				}
				ids[id] = true
			}
		}
	}
	if int64(len(roots)) < req.MinimumRootVersion {
		return nil, errors.New("authority root is older than the independent recovery checkpoint")
	}
	return roots, nil
}

func seedKey(seed string) (ed25519.PrivateKey, error) {
	bytes, err := base64.StdEncoding.DecodeString(seed)
	defer clear(bytes)
	if err != nil || len(bytes) != ed25519.SeedSize {
		return nil, errors.New("invalid signing key")
	}
	return ed25519.NewKeyFromSeed(bytes), nil
}

// Rotation is built in a new directory. Publication and the authority checkpoint select it only after verification.
func rotate(req Request) (Result, error) {
	if len(req.Keys) != 0 || len(req.Packages) != 0 || len(req.RootSigners) < 1 || len(req.RootSigners) > 5 ||
		!filepath.IsAbs(req.Destination) || cleanPath(req.Destination) != nil || req.Destination == req.Repository {
		return Result{}, errors.New("rotation needs a fresh destination and explicit offline root signers")
	}
	if _, err := os.Stat(req.Destination); !os.IsNotExist(err) {
		return Result{}, errors.New("rotation destination already exists")
	}
	lock, err := os.OpenFile(filepath.Join(req.Repository, ".publisher.lock"), os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
	if err != nil {
		return Result{}, err
	}
	defer func() { lock.Close(); os.Remove(lock.Name()) }()
	verified, err := check(req)
	if err != nil {
		return Result{}, err
	}
	roots, err := trustedRoots(req)
	if err != nil {
		return Result{}, err
	}
	previous := roots[len(roots)-1]
	if previous.Signed.Version >= 32 {
		return Result{}, errors.New("root-chain bound reached")
	}
	threshold := req.RootThreshold
	if threshold == 0 {
		threshold = 2
	}
	if threshold < 1 || threshold > 3 {
		return Result{}, errors.New("choose a root threshold between one and three")
	}
	next := metadata.Root(time.Now().UTC().Add(365 * 24 * time.Hour))
	next.Signed.UnrecognizedFields = map[string]any{channelField: feedChannel(req)}
	next.Signed.Version = previous.Signed.Version + 1
	keys := map[string]string{}
	var newRootSigners []ed25519.PrivateKey
	defer func() {
		for _, key := range newRootSigners {
			clear(key)
		}
	}()
	for _, role := range []string{"root", "targets", "snapshot", "timestamp"} {
		count := 1
		if role == "root" {
			count = 3
		}
		for i := 0; i < count; i++ {
			_, key, err := ed25519.GenerateKey(nil)
			if err != nil {
				return Result{}, err
			}
			label := role
			if role == "root" {
				label = fmt.Sprintf("root-%d", i+1)
			}
			keys[label] = base64.StdEncoding.EncodeToString(key.Seed())
			pub, err := metadata.KeyFromPublicKey(key.Public())
			if err == nil {
				err = next.Signed.AddKey(pub, role)
			}
			if err != nil {
				clear(key)
				return Result{}, err
			}
			if role == "root" {
				newRootSigners = append(newRootSigners, key)
			} else {
				clear(key)
			}
		}
	}
	next.Signed.Roles["root"].Threshold = threshold
	for _, seed := range req.RootSigners {
		key, err := seedKey(seed)
		if err != nil {
			return Result{}, err
		}
		signer, err := signature.LoadSigner(key, crypto.Hash(0))
		if err == nil {
			_, err = next.Sign(signer)
		}
		clear(key)
		if err != nil {
			return Result{}, err
		}
	}
	if err = previous.VerifyDelegate("root", next); err != nil {
		return Result{}, fmt.Errorf("old root threshold: %w", err)
	}
	for _, key := range newRootSigners {
		signer, err := signature.LoadSigner(key, crypto.Hash(0))
		if err == nil {
			_, err = next.Sign(signer)
		}
		if err != nil {
			return Result{}, err
		}
	}
	if err = next.VerifyDelegate("root", next); err != nil {
		return Result{}, err
	}
	for _, relative := range verified.Files {
		source, err := os.Open(filepath.Join(req.Repository, filepath.FromSlash(relative)))
		if err != nil {
			return Result{}, err
		}
		path := filepath.Join(req.Destination, filepath.FromSlash(relative))
		err = os.MkdirAll(filepath.Dir(path), 0700)
		if err == nil {
			var output *os.File
			output, err = os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0600)
			if err == nil {
				_, err = io.Copy(output, source)
				err = errors.Join(err, output.Sync(), output.Close())
			}
		}
		err = errors.Join(err, source.Close())
		if err != nil {
			return Result{}, err
		}
	}
	bytes, err := next.ToBytes(true)
	if err != nil {
		return Result{}, err
	}
	if err = immutable(filepath.Join(req.Destination, "metadata", fmt.Sprintf("%d.root.json", next.Signed.Version)), bytes); err != nil {
		return Result{}, err
	}
	online := map[string]string{}
	for _, role := range []string{"targets", "snapshot", "timestamp"} {
		online[role] = keys[role]
	}
	rotated := Request{Operation: "renew", Repository: req.Destination, BootstrapHash: req.BootstrapHash, Keys: online, Channel: feedChannel(req)}
	if _, err = publishWithPrevious(rotated, time.Now().UTC(), previous); err != nil {
		return Result{}, err
	}
	result, err := check(rotated)
	if err != nil {
		return Result{}, err
	}
	result.Keys = keys
	return result, nil
}

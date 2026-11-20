package staging

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"path/filepath"
	"strings"

	"filippo.io/age"
	"github.com/theupdateframework/go-tuf/v2/metadata"
)

type backupEnvelope struct {
	Schema          int               `json:"schema"`
	BootstrapHash   string            `json:"bootstrapHash"`
	RootVersion     int64             `json:"rootVersion"`
	MetadataVersion int64             `json:"metadataVersion"`
	Keys            map[string]string `json:"keys"`
}

func validateCustodyKeys(req Request, keys map[string]string) error {
	roots, err := trustedRoots(req)
	if err != nil {
		return err
	}
	root := roots[len(roots)-1]
	probe := metadata.Root()
	rootCount := 0
	for label, seed := range keys {
		if !roleLabelPattern.MatchString(label) {
			return errors.New("invalid backup role label")
		}
		role := label
		if label == "root" || strings.HasPrefix(label, "root-") {
			role = "root"
			rootCount++
		}
		if role != "root" && role != "targets" && role != "snapshot" && role != "timestamp" {
			return errors.New("unknown backup role")
		}
		key, err := seedKey(seed)
		if err != nil {
			return err
		}
		pub, err := metadata.KeyFromPublicKey(key.Public())
		clear(key)
		if err != nil {
			return err
		}
		id, err := pub.ID()
		if err != nil {
			return err
		}
		found := false
		for _, allowed := range root.Signed.Roles[role].KeyIDs {
			if allowed == id {
				found = true
			}
		}
		if !found {
			return errors.New("backup contains a revoked or unrelated key")
		}
		if err = probe.Signed.AddKey(pub, role); err != nil {
			return err
		}
	}
	if rootCount != len(root.Signed.Roles["root"].KeyIDs) || len(keys) != rootCount+3 {
		return errors.New("backup must contain every current authority key")
	}
	for _, role := range []string{"root", "targets", "snapshot", "timestamp"} {
		if len(probe.Signed.Roles[role].KeyIDs) != len(root.Signed.Roles[role].KeyIDs) {
			return errors.New("duplicate or missing backup signer")
		}
	}
	return nil
}

func custody(req Request) (Result, error) {
	if len(req.Passphrase) < 16 || len(req.Passphrase) > 1024 || !filepath.IsAbs(req.Backup) || cleanPath(req.Backup) != nil || len(req.Packages) != 0 || len(req.RootSigners) != 0 {
		return Result{}, errors.New("custody needs a direct backup path and a passphrase of at least 16 characters")
	}
	relative, err := filepath.Rel(req.Repository, req.Backup)
	if err == nil && relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator)) && !filepath.IsAbs(relative) {
		return Result{}, errors.New("keep encrypted key backups outside the public repository")
	}
	verified, err := check(req)
	if err != nil {
		return Result{}, err
	}
	if verified.Version < req.MinimumVersion {
		return Result{}, errors.New("public repository is older than the independent recovery checkpoint")
	}
	if req.Operation == "backup" {
		if err = validateCustodyKeys(req, req.Keys); err != nil {
			return Result{}, err
		}
		plain, err := json.Marshal(backupEnvelope{1, verified.BootstrapHash, verified.RootVersion, verified.Version, req.Keys})
		if err != nil {
			return Result{}, err
		}
		defer clear(plain)
		recipient, err := age.NewScryptRecipient(req.Passphrase)
		if err != nil {
			return Result{}, err
		}
		var encrypted bytes.Buffer
		writer, err := age.Encrypt(&encrypted, recipient)
		if err != nil {
			return Result{}, err
		}
		if _, err = writer.Write(plain); err != nil {
			return Result{}, err
		}
		if err = writer.Close(); err != nil {
			return Result{}, err
		}
		if err = immutable(req.Backup, encrypted.Bytes()); err != nil {
			return Result{}, err
		}
		// Read back and authenticate the encrypted file before acknowledging a successful backup.
		req.Operation = "restore"
		req.Keys = nil
		if _, err = custody(req); err != nil {
			return Result{}, err
		}
		return verified, nil
	}
	if len(req.Keys) != 0 {
		return Result{}, errors.New("restore cannot accept replacement keys")
	}
	encrypted, err := read(req.Backup, 65536)
	if err != nil {
		return Result{}, err
	}
	identity, err := age.NewScryptIdentity(req.Passphrase)
	if err != nil {
		return Result{}, err
	}
	identity.SetMaxWorkFactor(18)
	reader, err := age.Decrypt(bytes.NewReader(encrypted), identity)
	if err != nil {
		return Result{}, errors.New("backup authentication failed")
	}
	plain, err := io.ReadAll(io.LimitReader(reader, 32769))
	defer clear(plain)
	if err != nil || len(plain) > 32768 {
		return Result{}, errors.New("backup authentication or length check failed")
	}
	decoder := json.NewDecoder(bytes.NewReader(plain))
	decoder.DisallowUnknownFields()
	var envelope backupEnvelope
	if err = decoder.Decode(&envelope); err != nil || decoder.Decode(new(any)) != io.EOF {
		return Result{}, errors.New("invalid authority backup")
	}
	if envelope.Schema != 1 || envelope.BootstrapHash != verified.BootstrapHash || envelope.RootVersion != verified.RootVersion || envelope.MetadataVersion > verified.Version {
		return Result{}, errors.New("backup does not match the current verified authority and publication floor")
	}
	if err = validateCustodyKeys(req, envelope.Keys); err != nil {
		return Result{}, err
	}
	verified.Keys = envelope.Keys
	return verified, nil
}

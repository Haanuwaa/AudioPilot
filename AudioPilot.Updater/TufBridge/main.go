package main

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
	"strconv"
	"strings"
	"time"

	"audiopilot.local/tufbridge/internal/pathguard"

	"github.com/theupdateframework/go-tuf/v2/metadata"
	"github.com/theupdateframework/go-tuf/v2/metadata/config"
	"github.com/theupdateframework/go-tuf/v2/metadata/updater"
)

// Local/loopback transports and a separately pinned Sandbox-only public staging transport.
type request struct {
	Schema           int    `json:"schema"`
	Operation        string `json:"operation"`
	Nonce            string `json:"nonce"`
	Repository       string `json:"repository"`
	Cache            string `json:"cache"`
	BootstrapHash    string `json:"bootstrapHash"`
	Target           string `json:"target"`
	Package          string `json:"package"`
	Kind             string `json:"kind"`
	Origin           string `json:"origin"`
	TLSRoot          string `json:"tlsRoot"`
	TLSRootHash      string `json:"tlsRootHash"`
	Transport        string `json:"transport"`
	Architecture     string `json:"architecture"`
	Runtime          string `json:"runtime"`
	InstalledVersion string `json:"installedVersion"`
	HelperVersion    string `json:"helperVersion"`
	Channel          string `json:"channel"`
}
type targetIdentity struct {
	Product                string `json:"product"`
	Channel                string `json:"channel"`
	Version                string `json:"version"`
	Kind                   string `json:"kind"`
	Architecture           string `json:"architecture"`
	Runtime                string `json:"runtime"`
	MinimumHelperVersion   string `json:"minimumHelperVersion"`
	WindowsPublisherSHA256 string `json:"windowsPublisherSha256,omitempty"`
	WindowsTrust           string `json:"windowsTrust,omitempty"`
}
type response struct {
	Schema        int             `json:"schema"`
	Nonce         string          `json:"nonce"`
	BootstrapHash string          `json:"bootstrapHash"`
	Target        string          `json:"target"`
	Length        int64           `json:"length"`
	SHA256        string          `json:"sha256"`
	Custom        *targetIdentity `json:"custom"`
	Library       string          `json:"library"`
	Status        string          `json:"status,omitempty"`
	Version       string          `json:"version,omitempty"`
}

func main() {
	if err := run(os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
func run(input io.Reader, output io.Writer) error {
	data, err := io.ReadAll(io.LimitReader(input, 16385))
	if err != nil {
		return err
	}
	if len(data) > 16384 {
		return errors.New("oversized TUF bridge request")
	}
	var req request
	decoder := json.NewDecoder(strings.NewReader(string(data)))
	decoder.DisallowUnknownFields()
	if err = decoder.Decode(&req); err != nil {
		return err
	}
	if decoder.Decode(new(any)) != io.EOF {
		return errors.New("trailing bridge request data")
	}
	if req.Schema != 1 || len(req.Nonce) != 64 {
		return errors.New("invalid local bridge protocol")
	}
	if _, err = hex.DecodeString(req.Nonce); err != nil {
		return err
	}
	var result response
	switch req.Operation {
	case "fixture":
		result, err = createFixture(req)
	case "verify", "select":
		result, err = verify(req)
	default:
		return errors.New("unsupported local bridge operation")
	}
	if err != nil {
		return err
	}
	result.Schema = 1
	result.Nonce = req.Nonce
	result.Library = "go-tuf/v2 v2.4.2"
	return json.NewEncoder(output).Encode(result)
}

func safePath(path string) (string, error) {
	if err := pathguard.Check(path); err != nil {
		return "", err
	}
	return filepath.Clean(path), nil
}
func boundedRead(path string, max int64) ([]byte, error) {
	if _, err := safePath(path); err != nil {
		return nil, err
	}
	file, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer file.Close()
	info, err := file.Stat()
	if err != nil {
		return nil, err
	}
	if !info.Mode().IsRegular() || info.Size() > max {
		return nil, errors.New("invalid or oversized TUF metadata file")
	}
	data, err := io.ReadAll(io.LimitReader(file, max+1))
	if len(data) > int(max) {
		return nil, errors.New("TUF metadata exceeded its bound")
	}
	return data, err
}

type localFetcher struct{ repository string }

type checkpointFetcher struct {
	source interface {
		DownloadFile(string, int64, time.Duration) ([]byte, error)
	}
	checkpoint *trustCheckpoint
}

func (f *checkpointFetcher) DownloadFile(location string, max int64, timeout time.Duration) ([]byte, error) {
	if err := f.checkpoint.capture(); err != nil {
		return nil, err
	}
	data, err := f.source.DownloadFile(location, max, timeout)
	if err != nil {
		return nil, err
	}
	u, err := url.Parse(location)
	if err != nil {
		return nil, err
	}
	role := strings.TrimSuffix(filepath.Base(u.Path), ".json")
	if index := strings.IndexByte(role, '.'); index > 0 {
		if _, e := strconv.ParseInt(role[:index], 10, 64); e == nil {
			role = role[index+1:]
		}
	}
	// Invalid incoming documents are left to the maintained verifier to reject.
	_ = f.checkpoint.remember(role, data)
	return data, nil
}

func (f *localFetcher) DownloadFile(location string, max int64, _ time.Duration) ([]byte, error) {
	u, err := url.Parse(location)
	if err != nil {
		return nil, err
	}
	if u.Scheme != "https" || u.Host != "audiopilot-local.invalid" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || !strings.HasPrefix(u.Path, "/metadata/") {
		return nil, errors.New("network access is disabled in the local TUF bridge")
	}
	name := strings.TrimPrefix(u.Path, "/metadata/")
	if name == "" || filepath.Base(name) != name || strings.ContainsAny(name, "\\:") {
		return nil, errors.New("unsafe TUF metadata path")
	}
	data, err := boundedRead(filepath.Join(f.repository, "metadata", name), max)
	if os.IsNotExist(err) {
		return nil, &metadata.ErrDownloadHTTP{StatusCode: 404, URL: location}
	}
	return data, err
}
func verify(req request) (response, error) {
	var empty response
	repo, err := safePath(req.Repository)
	if err != nil {
		return empty, err
	}
	cache, err := safePath(req.Cache)
	if err != nil {
		return empty, err
	}
	if cache == repo || (req.Operation != "select" && !validTarget(req.Target)) {
		return empty, errors.New("invalid TUF cache or target")
	}
	bootstrap, err := boundedRead(filepath.Join(repo, "bootstrap.root.json"), 65536)
	if err != nil {
		return empty, err
	}
	hash := sha256.Sum256(bootstrap)
	if strings.ToUpper(hex.EncodeToString(hash[:])) != req.BootstrapHash {
		return empty, errors.New("bootstrap root pin mismatch")
	}
	store, err := openTrustStore(cache, req.BootstrapHash, bootstrap)
	if err != nil {
		return empty, err
	}
	defer store.file.Close()
	if err = cleanWorkingCaches(cache); err != nil {
		return empty, err
	}
	checkpoint, err := newTrustCheckpoint(store)
	if err != nil {
		return empty, err
	}
	working, err := os.MkdirTemp(cache, "work-")
	if err != nil {
		return empty, err
	}
	defer os.RemoveAll(working)
	for role, data := range store.latest {
		if err = os.WriteFile(filepath.Join(working, role+".json"), data, 0600); err != nil {
			return empty, err
		}
	}
	location := "https://audiopilot-local.invalid/metadata/"
	var source interface {
		DownloadFile(string, int64, time.Duration) ([]byte, error)
	} = &localFetcher{repository: repo}
	if req.Origin != "" {
		if req.Transport == "stable-test" {
			if sandboxAcceptance != "1" || os.Getenv("USERNAME") != "WDAGUtilityAccount" || req.BootstrapHash != stableRoot || len(stableRoot) != 64 {
				return empty, errors.New("stable loopback acceptance is Sandbox-only and root-bound")
			}
			network, e := newHTTPSFetcher(req)
			if e != nil {
				return empty, e
			}
			defer network.client.CloseIdleConnections()
			source = network
			location = req.Origin + "metadata/"
		} else if req.Transport == "staging" || req.Transport == "pilot" || req.Transport == "stable" {
			network, e := newStagingFetcher(req)
			if e != nil {
				return empty, e
			}
			defer network.client.CloseIdleConnections()
			source = network
			location = req.Origin
		} else if req.Transport == "" {
			network, e := newHTTPSFetcher(req)
			if e != nil {
				return empty, e
			}
			defer network.client.CloseIdleConnections()
			source = network
			location = req.Origin + "metadata/"
		} else {
			return empty, errors.New("unsupported TUF transport")
		}
	} else if req.Transport != "" {
		return empty, errors.New("explicit transport requires its bound origin")
	}
	cfg, err := config.New(location, store.latest["root"])
	if err != nil {
		return empty, err
	}
	cfg.Fetcher = &checkpointFetcher{source: source, checkpoint: checkpoint}
	cfg.LocalMetadataDir = working
	cfg.LocalTargetsDir = filepath.Join(working, "targets")
	cfg.DisableLocalCache = true
	cfg.MaxRootRotations = 32
	cfg.MaxDelegations = 8
	cfg.RootMaxLength = 65536
	cfg.TimestampMaxLength = 16384
	cfg.SnapshotMaxLength = 131072
	cfg.TargetsMaxLength = 131072
	client, err := updater.New(cfg)
	if err != nil {
		return empty, err
	}
	checkpoint.client = client
	err = client.Refresh()
	if persistErr := checkpoint.capture(); persistErr != nil {
		return empty, errors.Join(err, persistErr)
	}
	if err != nil {
		return empty, err
	}
	if req.Operation == "select" {
		result, err := selectStable(req, client.GetTopLevelTargets())
		result.BootstrapHash = req.BootstrapHash
		return result, err
	}
	target, err := client.GetTargetInfo(req.Target)
	if persistErr := checkpoint.capture(); persistErr != nil {
		return empty, errors.Join(err, persistErr)
	}
	if err != nil {
		return empty, err
	}
	if target.Custom == nil || target.Length <= 0 || target.Length > 1024*1024*1024 || len(target.Hashes["sha256"]) != 32 {
		return empty, errors.New("invalid signed target identity")
	}
	var custom targetIdentity
	d := json.NewDecoder(strings.NewReader(string(*target.Custom)))
	d.DisallowUnknownFields()
	if err = d.Decode(&custom); err != nil {
		return empty, err
	}
	return response{BootstrapHash: req.BootstrapHash, Target: req.Target, Length: target.Length, SHA256: strings.ToUpper(hex.EncodeToString(target.Hashes["sha256"])), Custom: &custom}, nil
}
func validTarget(name string) bool {
	if len(name) == 0 || len(name) > 160 || strings.HasPrefix(name, ".") || strings.Contains(name, "..") {
		return false
	}
	for _, c := range name {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '.' || c == '-' || c == '_') {
			return false
		}
	}
	return strings.HasSuffix(name, ".msi") || strings.HasSuffix(name, ".zip")
}

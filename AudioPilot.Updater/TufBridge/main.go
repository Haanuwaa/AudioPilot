package main

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"net/url"
	"os"
	"path/filepath"
	"runtime/debug"
	"strings"
	"time"

	"audiopilot.local/tufbridge/internal/pathguard"

	"github.com/theupdateframework/go-tuf/v2/metadata"
	"github.com/theupdateframework/go-tuf/v2/metadata/config"
	"github.com/theupdateframework/go-tuf/v2/metadata/fetcher"
	"github.com/theupdateframework/go-tuf/v2/metadata/updater"
)

// Policy-gated TUF bridge request across production stable, staging, pilot, and test transports.
type request struct {
	Schema           int    `json:"schema"`
	Operation        string `json:"operation"`
	Nonce            string `json:"nonce"`
	Repository       string `json:"repository"`
	Cache            string `json:"cache"`
	BootstrapHash    string `json:"bootstrapHash"`
	Target           string `json:"target"`
	Package          string `json:"package"`
	DeltaPackage     string `json:"deltaPackage"`
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
	Delta         *deltaTarget    `json:"delta,omitempty"`
	Library       string          `json:"library"`
	Status        string          `json:"status,omitempty"`
	Version       string          `json:"version,omitempty"`
}

func main() {
	if err := run(os.Stdin, os.Stdout); err != nil {
		_ = writeFailure(os.Stderr, err)
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
		if stableRoot != "" && sandboxAcceptance != "1" {
			return errors.New("test fixture creation is disabled in the stable bridge")
		}
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
	result.Library = goTufLibraryIdentity()
	return json.NewEncoder(output).Encode(result)
}

func goTufLibraryIdentity() string {
	const modulePath = "github.com/theupdateframework/go-tuf/v2"
	if info, ok := debug.ReadBuildInfo(); ok {
		for _, dep := range info.Deps {
			if dep != nil && dep.Path == modulePath && dep.Version != "" {
				return "go-tuf/v2 " + dep.Version
			}
		}
	}
	return "go-tuf/v2"
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

func (f *localFetcher) DownloadFileContext(ctx context.Context, location string, max int64) ([]byte, error) {
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	return f.DownloadFile(location, max, 0)
}

func (f *localFetcher) DownloadFile(location string, max int64, _ time.Duration) ([]byte, error) {
	if max <= 0 || max > 131072 {
		return nil, errors.New("invalid TUF metadata length bound")
	}
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
		return empty, &bridgeReasonError{"feed-trust-state", err}
	}
	defer store.file.Close()
	location := "https://audiopilot-local.invalid/metadata/"
	var source fetcher.ContextFetcher = &localFetcher{repository: repo}
	productionStable := stableRoot != "" && sandboxAcceptance != "1"
	if req.Origin != "" {
		if req.Transport == "stable-test" {
			if sandboxAcceptance != "1" || !isWindowsSandboxAccount() || req.BootstrapHash != stableRoot || len(stableRoot) != 64 {
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
			if productionStable {
				return empty, errors.New("loopback transport is disabled in the stable bridge")
			}
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
	} else if productionStable {
		return empty, errors.New("local metadata transport is disabled in the stable bridge")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 25*time.Second)
	defer cancel()
	cfg, err := config.New(location, bootstrap)
	if err != nil {
		return empty, err
	}
	cfg.Fetcher = source
	cfg.MetadataStore = store
	cfg.DisableLocalCache = false
	cfg.MaxRootRotations = 32
	cfg.MaxDelegations = 8
	cfg.RootMaxLength = 65536
	cfg.TimestampMaxLength = 16384
	cfg.SnapshotMaxLength = 131072
	cfg.TargetsMaxLength = 131072
	client, err := updater.NewContext(ctx, cfg)
	if err != nil {
		return empty, err
	}
	if err = client.RefreshContext(ctx); err != nil {
		return empty, err
	}
	if req.Operation == "select" {
		result, err := selectStable(req, client.GetTopLevelTargets())
		result.BootstrapHash = req.BootstrapHash
		return result, err
	}
	if req.Transport == "stable" || req.Transport == "stable-test" {
		if _, exists := client.GetTopLevelTargets()[req.Target]; !exists {
			return empty, &bridgeReasonError{"update-no-longer-offered", errors.New("the selected target left the active catalog")}
		}
	}
	target, err := client.GetTargetInfoContext(ctx, req.Target)
	if err != nil {
		return empty, err
	}
	result, err := targetResponse(req.Target, target)
	if err != nil {
		return empty, err
	}
	result.BootstrapHash = req.BootstrapHash
	result.Delta = portableDelta(req, result, client.GetTopLevelTargets())
	return result, nil
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

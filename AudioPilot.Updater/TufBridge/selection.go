package main

import (
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"strconv"
	"strings"

	"github.com/theupdateframework/go-tuf/v2/metadata"
)

type releaseVersion [3]int64

func parseReleaseVersion(value string) (releaseVersion, error) {
	var result releaseVersion
	parts := strings.Split(value, ".")
	if len(parts) != 3 {
		return result, errors.New("version must have three numeric components")
	}
	for i, part := range parts {
		n, err := strconv.ParseInt(part, 10, 32)
		if err != nil || n < 0 || strconv.FormatInt(n, 10) != part {
			return result, errors.New("noncanonical version")
		}
		result[i] = n
	}
	return result, nil
}
func (v releaseVersion) compare(other releaseVersion) int {
	for i := range v {
		if v[i] < other[i] {
			return -1
		}
		if v[i] > other[i] {
			return 1
		}
	}
	return 0
}

func targetResponse(name string, target *metadata.TargetFiles) (response, error) {
	if !validTarget(name) || target == nil || target.Custom == nil || target.Length <= 0 || target.Length > 1024*1024*1024 || len(target.Hashes["sha256"]) != 32 {
		return response{}, errors.New("invalid signed release package")
	}
	var identity targetIdentity
	d := json.NewDecoder(strings.NewReader(string(*target.Custom)))
	d.DisallowUnknownFields()
	if err := d.Decode(&identity); err != nil {
		return response{}, err
	}
	if d.Decode(new(any)) != io.EOF {
		return response{}, errors.New("trailing target identity data")
	}
	return response{Target: name, Length: target.Length, SHA256: strings.ToUpper(hex.EncodeToString(target.Hashes["sha256"])), Custom: &identity}, nil
}

// Called only with the maintained client's authenticated top-level targets after Refresh succeeds.
func selectStable(req request, targets map[string]*metadata.TargetFiles) (response, error) {
	installed, err := parseReleaseVersion(req.InstalledVersion)
	if err != nil {
		return response{}, err
	}
	helper, err := parseReleaseVersion(req.HelperVersion)
	if err != nil {
		return response{}, err
	}
	if req.Channel != "stable" || !validVariant(req.Kind, req.Architecture, req.Runtime) {
		return response{}, errors.New("unsupported stable selection")
	}
	var latest releaseVersion
	found := false
	candidates := make([]response, 0, len(targets))
	for name, target := range targets {
		candidate, e := targetResponse(name, target)
		if e != nil {
			return response{}, e
		}
		id := candidate.Custom
		if id.Product != "AudioPilot" || id.Channel != "stable" {
			continue
		}
		version, e := parseReleaseVersion(id.Version)
		if e != nil {
			return response{}, e
		}
		suffix := ".zip"
		if id.Kind == "msi" {
			suffix = ".msi"
		}
		if !validVariant(id.Kind, id.Architecture, id.Runtime) || !strings.HasSuffix(name, suffix) {
			return response{}, errors.New("invalid stable variant")
		}
		if _, e = parseReleaseVersion(id.MinimumHelperVersion); e != nil {
			return response{}, e
		}
		if !found || version.compare(latest) > 0 {
			latest, found = version, true
		}
		candidates = append(candidates, candidate)
	}
	var selected *response
	for _, candidate := range candidates {
		id := candidate.Custom
		version, _ := parseReleaseVersion(id.Version)
		if version != latest || id.Kind != req.Kind || id.Architecture != req.Architecture || id.Runtime != req.Runtime {
			continue
		}
		if selected != nil {
			return response{}, errors.New("ambiguous stable package variant")
		}
		candidate.Status = "available"
		minimum, _ := parseReleaseVersion(id.MinimumHelperVersion)
		if minimum.compare(helper) > 0 {
			candidate.Status = "bootstrap-required"
		}
		selected = &candidate
	}
	if !found {
		return response{Status: "no-release"}, nil
	}
	version := strconv.FormatInt(latest[0], 10) + "." + strconv.FormatInt(latest[1], 10) + "." + strconv.FormatInt(latest[2], 10)
	if latest.compare(installed) <= 0 {
		return response{Status: "current", Version: version}, nil
	}
	if selected == nil {
		return response{Status: "variant-unavailable", Version: version}, nil
	}
	selected.Version = version
	return *selected, nil
}

func validVariant(kind, architecture, runtime string) bool {
	return (kind == "portable" || kind == "msi") && (architecture == "win-x64" || architecture == "win-x86" || architecture == "win-arm64") &&
		(runtime == "self-contained" || kind == "portable" && runtime == "framework-dependent")
}

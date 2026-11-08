package main

import (
	"encoding/json"
	"fmt"
	"strings"
	"testing"

	"github.com/theupdateframework/go-tuf/v2/metadata"
)

func addStableTarget(t *testing.T, f *repositoryFixture, name, version, kind, arch, runtime, minimum string) {
	t.Helper()
	id := targetIdentity{Product: "AudioPilot", Channel: "stable", Version: version, Kind: kind, Architecture: arch, Runtime: runtime,
		MinimumHelperVersion: minimum, WindowsPublisherSHA256: strings.Repeat("A", 64), WindowsTrust: "self-signed"}
	data, err := json.Marshal(id)
	if err != nil {
		t.Fatal(err)
	}
	raw := json.RawMessage(data)
	target := metadata.TargetFile()
	target.Custom = &raw
	target.Length = 7
	target.Hashes = metadata.Hashes{"sha256": make([]byte, 32)}
	f.targets.Signed.Targets[name] = target
}

func TestStableSelectionUsesAuthenticatedLatestAndExactVariant(t *testing.T) {
	for _, arch := range []string{"win-x64", "win-x86", "win-arm64"} {
		for _, variant := range [][2]string{{"portable", "self-contained"}, {"portable", "framework-dependent"}, {"msi", "self-contained"}} {
			t.Run(arch+"/"+variant[0]+"/"+variant[1], func(t *testing.T) {
				f, req := setupRepository(t)
				req.Operation, req.Channel, req.Kind, req.Architecture, req.Runtime = "select", "stable", variant[0], arch, variant[1]
				req.InstalledVersion, req.HelperVersion = "1.0.0", "1.0.0"
				suffix := "zip"
				if variant[0] == "msi" {
					suffix = "msi"
				}
				name := "release-1.0.10." + suffix
				addStableTarget(t, f, "old."+suffix, "1.0.9", variant[0], arch, variant[1], "1.0.0")
				addStableTarget(t, f, name, "1.0.10", variant[0], arch, variant[1], "1.0.0")
				addStableTarget(t, f, "prerelease.zip", "99.0.0", "portable", arch, variant[1], "1.0.0")
				var id targetIdentity
				_ = json.Unmarshal(*f.targets.Signed.Targets["prerelease.zip"].Custom, &id)
				id.Channel = "staging"
				data, _ := json.Marshal(id)
				raw := json.RawMessage(data)
				f.targets.Signed.Targets["prerelease.zip"].Custom = &raw
				writeFixture(t, f, req.Repository)
				result, err := verify(req)
				if err != nil || result.Status != "available" || result.Target != name || result.Custom.Version != "1.0.10" {
					t.Fatalf("wrong authenticated selection: %+v %v", result, err)
				}
			})
		}
	}
}

func TestStableSelectionDoesNotSilentlyDowngradeOrChangeVariant(t *testing.T) {
	for _, scenario := range []string{"current", "missing", "helper", "duplicate", "invalid-version", "expired", "tampered"} {
		t.Run(scenario, func(t *testing.T) {
			f, req := setupRepository(t)
			req.Operation, req.Channel, req.Kind, req.Architecture, req.Runtime = "select", "stable", "portable", "win-x64", "self-contained"
			req.InstalledVersion, req.HelperVersion = "1.0.0", "1.0.0"
			addStableTarget(t, f, "old.zip", "1.0.1", "portable", "win-x64", "self-contained", "1.0.0")
			arch, minimum, version := "win-x64", "1.0.0", "1.0.2"
			switch scenario {
			case "current":
				req.InstalledVersion = "1.0.2"
			case "missing":
				arch = "win-arm64"
			case "helper":
				minimum = "2.0.0"
			case "invalid-version":
				version = "1.0.2-beta"
			}
			addStableTarget(t, f, "new.zip", version, "portable", arch, "self-contained", minimum)
			if scenario == "duplicate" {
				addStableTarget(t, f, "duplicate.zip", version, "portable", arch, "self-contained", minimum)
			}
			if scenario == "expired" {
				f.timestamp.Signed.Expires = f.timestamp.Signed.Expires.AddDate(-1, 0, 0)
			}
			writeFixture(t, f, req.Repository)
			if scenario == "tampered" {
				f.targets.Signed.Version++
				if err := f.targets.ToFile(fmt.Sprintf("%s/metadata/1.targets.json", req.Repository), true); err != nil {
					t.Fatal(err)
				}
			}
			result, err := verify(req)
			if scenario == "duplicate" || scenario == "invalid-version" || scenario == "expired" || scenario == "tampered" {
				if err == nil {
					t.Fatal("invalid discovery was accepted")
				}
				return
			}
			want := map[string]string{"current": "current", "missing": "variant-unavailable", "helper": "bootstrap-required"}[scenario]
			if err != nil || result.Status != want {
				t.Fatalf("got %+v %v, want %s", result, err, want)
			}
		})
	}
}

func TestStableTransportRemainsDisabledOutsideSandboxAndRootBound(t *testing.T) {
	previous := stableRoot
	previousEnabled := stableEnabled
	t.Cleanup(func() { stableRoot = previous; stableEnabled = previousEnabled })
	stableEnabled = ""
	stableRoot = strings.Repeat("A", 64)
	req := request{Transport: "stable", Origin: stableOrigin, BootstrapHash: stableRoot}
	t.Setenv("USERNAME", "ordinary-user")
	if _, err := newStagingFetcher(req); err == nil {
		t.Fatal("public stable transport enabled")
	}
	t.Setenv("USERNAME", "WDAGUtilityAccount")
	client, err := newStagingFetcher(req)
	if err != nil {
		t.Fatal(err)
	}
	client.client.CloseIdleConnections()
	t.Setenv("USERNAME", "ordinary-user")
	stableEnabled = "1"
	client, err = newStagingFetcher(req)
	if err != nil {
		t.Fatal("explicit public bootstrap remained disabled", err)
	}
	client.client.CloseIdleConnections()
	req.BootstrapHash = strings.Repeat("B", 64)
	if _, err := newStagingFetcher(req); err == nil {
		t.Fatal("arbitrary root accepted")
	}
}

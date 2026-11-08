package staging

import (
	"path/filepath"
	"testing"
)

func TestStableAuthorityCannotAuthorizeStagingAndPreservesChannelAcrossRotation(t *testing.T) {
	repo := filepath.Join(t.TempDir(), "stable")
	created, err := Execute(Request{Operation: "initialize", Repository: repo, Channel: "stable"})
	if err != nil {
		t.Fatal(err)
	}
	keys := map[string]string{}
	for _, role := range []string{"targets", "snapshot", "timestamp"} {
		keys[role] = created.Keys[role]
	}
	pkg := candidate(t, "1.0.1")
	pkg.Identity.Channel, pkg.Identity.WindowsTrust = "stable", "self-signed"
	req := Request{Operation: "publish", Repository: repo, BootstrapHash: created.BootstrapHash, Channel: "stable", Keys: keys, Packages: []Package{pkg}}
	if _, err = Execute(req); err != nil {
		t.Fatal(err)
	}
	check := Request{Operation: "check", Repository: repo, BootstrapHash: created.BootstrapHash, Channel: "stable"}
	if verified, err := Execute(check); err != nil || verified.Channel != "stable" {
		t.Fatal("stable verification", verified, err)
	}
	check.Channel = "staging"
	if _, err = Execute(check); err == nil {
		t.Fatal("stable root accepted as staging")
	}
	req.Packages[0].Identity.Channel = "staging"
	if _, err = Execute(req); err == nil {
		t.Fatal("staging target accepted by stable authority")
	}
	rotated, err := Execute(Request{Operation: "rotate", Repository: repo, BootstrapHash: created.BootstrapHash, Channel: "stable", Destination: filepath.Join(t.TempDir(), "rotated"), RootSigners: []string{created.Keys["root"]}})
	if err != nil || rotated.Channel != "stable" || rotated.RootVersion != 2 {
		t.Fatal("stable rotation", rotated, err)
	}
}

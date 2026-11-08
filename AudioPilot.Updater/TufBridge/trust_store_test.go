package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

func openTestStore(t *testing.T, req request) *trustStore {
	t.Helper()
	bootstrap, err := os.ReadFile(filepath.Join(req.Repository, "bootstrap.root.json"))
	if err != nil {
		t.Fatal(err)
	}
	store, err := openTrustStore(req.Cache, req.BootstrapHash, bootstrap)
	if err != nil {
		t.Fatal(err)
	}
	return store
}
func TestTornTailRetainsCommittedTimestampFloor(t *testing.T) {
	f, req := setupRepository(t)
	f.timestamp.Signed.Version = 2
	writeFixture(t, f, req.Repository)
	requireVerified(t, req)
	path := filepath.Join(req.Cache, "trust.journal")
	before, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	file, err := os.OpenFile(path, os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		t.Fatal(err)
	}
	if _, err = file.Write([]byte{20, 0, 0}); err != nil {
		t.Fatal(err)
	}
	file.Close()
	store := openTestStore(t, req)
	store.file.Close()
	after, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(before, after) {
		t.Fatal("incomplete tail did not recover exactly")
	}
	f.timestamp.Signed.Version = 1
	writeFixture(t, f, req.Repository)
	requireRejected(t, req)
}
func TestCommittedCorruptionCannotFallBack(t *testing.T) {
	_, req := setupRepository(t)
	requireVerified(t, req)
	path := filepath.Join(req.Cache, "trust.journal")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	data[len(data)-10] ^= 1
	if err = os.WriteFile(path, data, 0600); err != nil {
		t.Fatal(err)
	}
	requireRejected(t, req)
}

func TestLosingCacheDirectoryOrAnchorCannotResetTrust(t *testing.T) {
	for _, missing := range []string{"directory", "anchor"} {
		t.Run(missing, func(t *testing.T) {
			_, req := setupRepository(t)
			requireVerified(t, req)
			var err error
			if missing == "directory" {
				err = os.RemoveAll(req.Cache)
			} else {
				err = os.Remove(req.Cache + ".anchor")
			}
			if err != nil {
				t.Fatal(err)
			}
			requireRejected(t, req)
		})
	}
}
func TestVerifiedTimestampSurvivesLaterRefreshFailure(t *testing.T) {
	f, req := setupRepository(t)
	requireVerified(t, req)
	f.timestamp.Signed.Version = 2
	f.snapshot.Signed.Version = 2
	writeFixture(t, f, req.Repository)
	if err := os.Remove(filepath.Join(req.Repository, "metadata", "2.snapshot.json")); err != nil {
		t.Fatal(err)
	}
	requireRejected(t, req)
	f.timestamp.Signed.Version = 1
	f.snapshot.Signed.Version = 1
	writeFixture(t, f, req.Repository)
	requireRejected(t, req)
}
func TestJournalFaultsNeverAuthorizeAndRecoverCompleteAdvances(t *testing.T) {
	for _, boundary := range []string{"before-write", "before-sync", "after-sync"} {
		t.Run(boundary, func(t *testing.T) {
			_, req := setupRepository(t)
			store := openTestStore(t, req)
			store.fault = func(stage string) error {
				if stage == boundary {
					return errors.New("injected disk failure")
				}
				return nil
			}
			data, err := os.ReadFile(filepath.Join(req.Repository, "metadata", "1.targets.json"))
			if err != nil {
				t.Fatal(err)
			}
			if err := store.append("targets", data); err == nil {
				t.Fatal("failed persistence returned success")
			}
			if err := store.append("targets", data); err == nil {
				t.Fatal("failed writer was reused")
			}
			store.file.Close()
			recovered := openTestStore(t, req)
			defer recovered.file.Close()
			if boundary != "before-write" && !bytes.Equal(recovered.latest["targets"], data) {
				t.Fatal("complete advance was forgotten")
			}
		})
	}
}

func TestProcessExitAfterFlushRetainsTimestampFloor(t *testing.T) {
	f, req := setupRepository(t)
	requireVerified(t, req)
	f.timestamp.Signed.Version = 2
	writeFixture(t, f, req.Repository)
	encoded, err := json.Marshal(req)
	if err != nil {
		t.Fatal(err)
	}
	command := exec.Command(os.Args[0], "-test.run=^TestJournalExitChild$")
	command.Env = append(os.Environ(), "AUDIOPILOT_JOURNAL_TEST="+string(encoded))
	err = command.Run()
	var exited *exec.ExitError
	if !errors.As(err, &exited) || exited.ExitCode() != 83 {
		t.Fatalf("child did not stop at the committed boundary: %v", err)
	}
	f.timestamp.Signed.Version = 1
	writeFixture(t, f, req.Repository)
	requireRejected(t, req)
}
func TestJournalExitChild(t *testing.T) {
	input := os.Getenv("AUDIOPILOT_JOURNAL_TEST")
	if input == "" {
		t.Skip("subprocess fixture")
	}
	var req request
	if err := json.Unmarshal([]byte(input), &req); err != nil {
		t.Fatal(err)
	}
	store := openTestStore(t, req)
	store.fault = func(stage string) error {
		if stage == "after-sync" {
			os.Exit(83)
		}
		return nil
	}
	data, err := os.ReadFile(filepath.Join(req.Repository, "metadata", "timestamp.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err = store.append("timestamp", data); err != nil {
		t.Fatal(err)
	}
	t.Fatal("injected process exit was not reached")
}
func TestJournalExcludesConcurrentWriters(t *testing.T) {
	_, req := setupRepository(t)
	store := openTestStore(t, req)
	defer store.file.Close()
	bootstrap, _ := os.ReadFile(filepath.Join(req.Repository, "bootstrap.root.json"))
	if other, err := openTrustStore(req.Cache, req.BootstrapHash, bootstrap); err == nil {
		other.file.Close()
		t.Fatal("concurrent journal writer was admitted")
	}
}

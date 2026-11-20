package pathguard

import (
	"os"
	"path/filepath"
	"testing"
)

func TestRejectsRedirectedAncestorWithoutRequiringTheLeaf(t *testing.T) {
	base := t.TempDir()
	if err := Check(filepath.Join(base, "missing", "leaf")); err != nil {
		t.Fatal(err)
	}
	link := filepath.Join(base, "redirect")
	if err := os.Symlink(t.TempDir(), link); err != nil {
		t.Skipf("symlink creation unavailable: %v", err)
	}
	if err := Check(filepath.Join(link, "missing", "leaf")); err == nil {
		t.Fatal("redirected ancestor accepted")
	}
}

package pathguard

import (
	"context"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	"golang.org/x/sys/windows"
)

func TestRejectsWindowsJunction(t *testing.T) {
	base := t.TempDir()
	junction := filepath.Join(base, "redirect")
	target := t.TempDir()
	quote := func(value string) string { return "'" + strings.ReplaceAll(value, "'", "''") + "'" }
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	command := "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path " + quote(junction) + " -Target " + quote(target) + " | Out-Null"
	if output, err := exec.CommandContext(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command).CombinedOutput(); err != nil {
		t.Fatalf("create owned junction: %v %s", err, output)
	}
	defer func() {
		if err := os.Remove(junction); err != nil {
			t.Error(err)
		}
	}()
	if err := Check(filepath.Join(junction, "missing", "leaf")); err == nil {
		t.Fatal("junction ancestor accepted")
	}
}

func TestAcceptsWindowsShortNameOfTheSameDirectory(t *testing.T) {
	directory := filepath.Join(t.TempDir(), "AudioPilot long directory name")
	if err := os.Mkdir(directory, 0700); err != nil {
		t.Fatal(err)
	}
	for _, path := range []string{directory, os.Getenv("ProgramFiles")} {
		if path == "" {
			continue
		}
		name, err := windows.UTF16PtrFromString(path)
		if err != nil {
			t.Fatal(err)
		}
		buffer := make([]uint16, 32768)
		length, err := windows.GetShortPathName(name, &buffer[0], uint32(len(buffer)))
		if err != nil || length >= uint32(len(buffer)) {
			t.Fatalf("short-name lookup: %v", err)
		}
		short := windows.UTF16ToString(buffer[:length])
		if strings.EqualFold(short, path) {
			continue
		}
		if err := Check(filepath.Join(short, "missing", "leaf")); err != nil {
			t.Fatalf("valid short-name alias rejected: %v", err)
		}
		return
	}
	t.Skip("short names are not enabled for the available directories")
}

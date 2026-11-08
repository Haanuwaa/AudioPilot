package main

import (
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"testing"
)

func TestMain(m *testing.M) {
	if output := os.Getenv("GO_LAUNCHER_HELPER_OUTPUT"); output != "" {
		data, err := json.Marshal(os.Args[1:])
		if err != nil || os.WriteFile(output, data, 0600) != nil {
			os.Exit(9)
		}
		os.Exit(7)
	}
	os.Exit(m.Run())
}

func TestLauncherWaitsForHelperAndPreservesArgumentsAndExitStatus(t *testing.T) {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	image, err := os.ReadFile(self)
	if err != nil {
		t.Fatal(err)
	}
	for _, cli := range []bool{false, true} {
		t.Run(map[bool]string{false: "GUI", true: "CLI"}[cli], func(t *testing.T) {
			root := t.TempDir()
			workspace := filepath.Join(root, "app")
			generation := "1.0.0_0123456789abcdef0123456789abcdef"
			folder := filepath.Join(workspace, "versions", generation)
			if err := os.MkdirAll(folder, 0700); err != nil {
				t.Fatal(err)
			}
			files := map[string][]byte{
				filepath.Join(workspace, ".updater"):        []byte("AudioPilot updater workspace 1"),
				filepath.Join(workspace, "deployment.json"): []byte(`{"Schema":1,"Current":"` + generation + `","Previous":null,"PendingHealth":false}`),
			}
			name, target := "AudioPilot.exe", "AudioPilot.Updater.exe"
			if cli {
				name, target = "AudioPilot.Cli.exe", "AudioPilot.Cli.exe"
			}
			files[filepath.Join(folder, target)] = image
			for path, data := range files {
				if err := os.WriteFile(path, data, 0700); err != nil {
					t.Fatal(err)
				}
			}
			output := filepath.Join(root, "arguments.json")
			t.Setenv("GO_LAUNCHER_HELPER_OUTPUT", output)
			arguments := []string{"with spaces", `embedded "quote"`, ""}
			err := run(filepath.Join(root, name), arguments)
			var exit *exec.ExitError
			if !errors.As(err, &exit) || exit.ExitCode() != 7 {
				t.Fatalf("helper exit was lost: %v", err)
			}
			data, err := os.ReadFile(output)
			if err != nil {
				t.Fatal(err)
			}
			var actual []string
			if err := json.Unmarshal(data, &actual); err != nil {
				t.Fatal(err)
			}
			expected := arguments
			if !cli {
				expected = append([]string{"pilot-launch", workspace}, arguments...)
			}
			if !reflect.DeepEqual(actual, expected) {
				t.Fatalf("arguments changed: got %#v, want %#v", actual, expected)
			}
		})
	}
}

func TestReadStateRejectsAmbiguityAndEscapingGenerations(t *testing.T) {
	valid := `{"Schema":1,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":false}`
	if _, err := readState([]byte(valid)); err != nil {
		t.Fatal(err)
	}
	for _, invalid := range []string{
		`{"Schema":1,"Schema":2,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":false}`,
		`{"Schema":1,"Current":"../elsewhere","Previous":null,"PendingHealth":false}`,
		`{"Schema":1,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":true}`,
		`{"Schema":1,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":null}`,
		valid + `{}`,
	} {
		if _, err := readState([]byte(invalid)); err == nil {
			t.Fatal("unsafe launcher state was accepted")
		}
	}
}

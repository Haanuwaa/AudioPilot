package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strconv"
	"strings"
	"testing"
)

func TestMain(m *testing.M) {
	if output := os.Getenv("GO_LAUNCHER_HELPER_OUTPUT"); output != "" {
		if cwd := os.Getenv("GO_LAUNCHER_HELPER_CWD"); cwd != "" {
			directory, err := os.Getwd()
			if err != nil || os.WriteFile(cwd, []byte(directory), 0600) != nil {
				os.Exit(9)
			}
		}
		data, err := json.Marshal(os.Args[1:])
		if err != nil || os.WriteFile(output, data, 0600) != nil {
			os.Exit(9)
		}
		if code, parseErr := strconv.Atoi(os.Getenv("GO_LAUNCHER_HELPER_EXIT")); parseErr == nil {
			os.Exit(code)
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
	for _, launcher := range []struct {
		name     string
		cli      bool
		elevated bool
	}{
		{name: "GUI"},
		{name: "elevated-GUI", elevated: true},
		{name: "CLI", cli: true, elevated: true},
	} {
		t.Run(launcher.name, func(t *testing.T) {
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
			if launcher.cli {
				name, target = "AudioPilot.Cli.exe", "AudioPilot.Cli.exe"
			}
			files[filepath.Join(folder, target)] = image
			for path, data := range files {
				if err := os.WriteFile(path, data, 0700); err != nil {
					t.Fatal(err)
				}
			}
			output := filepath.Join(root, "arguments.json")
			cwdOutput := filepath.Join(root, "cwd.txt")
			t.Setenv("GO_LAUNCHER_HELPER_OUTPUT", output)
			t.Setenv("GO_LAUNCHER_HELPER_CWD", cwdOutput)
			arguments := []string{"with spaces", `embedded "quote"`, ""}
			elevationChecks := 0
			checkElevation := func() error {
				elevationChecks++
				if launcher.elevated {
					return errElevated
				}
				return nil
			}
			err := run(filepath.Join(root, name), arguments, checkElevation)
			if launcher.cli && elevationChecks != 0 || !launcher.cli && elevationChecks != 1 {
				t.Fatalf("unexpected number of elevation checks: %d", elevationChecks)
			}
			if !launcher.cli && launcher.elevated {
				if !errors.Is(err, errElevated) {
					t.Fatalf("elevated launcher was not refused: %v", err)
				}
				if _, err := os.Stat(output); !errors.Is(err, os.ErrNotExist) {
					t.Fatalf("elevated launcher started the helper: %v", err)
				}
				return
			}
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
			if !launcher.cli {
				expected = append([]string{"pilot-launch", workspace}, arguments...)
			}
			if !reflect.DeepEqual(actual, expected) {
				t.Fatalf("arguments changed: got %#v, want %#v", actual, expected)
			}
			cwd, err := os.ReadFile(cwdOutput)
			if err != nil {
				t.Fatal(err)
			}
			expectedDirectory := folder
			if launcher.cli {
				expectedDirectory, err = os.Getwd()
				if err != nil {
					t.Fatal(err)
				}
			}
			if string(cwd) != expectedDirectory {
				t.Fatalf("working directory changed: got %s, want %s", cwd, expectedDirectory)
			}
			// During pending health, safe managed CLI controls remain reachable through the previous generation.
			if launcher.cli {
				pending := []byte(`{"Schema":1,"Current":"1.0.1_1123456789abcdef0123456789abcdef","Previous":"` + generation + `","PendingHealth":true}`)
				if err = os.WriteFile(filepath.Join(workspace, "deployment.json"), pending, 0600); err != nil {
					t.Fatal(err)
				}
				err = run(filepath.Join(root, name), []string{"update", "status", "--json"}, checkElevation)
				if !errors.As(err, &exit) || exit.ExitCode() != 7 {
					t.Fatalf("safe recovery CLI could not start: %v", err)
				}
				if elevationChecks != 0 {
					t.Fatal("safe recovery CLI checked elevation")
				}
			}
		})
	}
}

func TestPreflightErrorsUseCliEnvelopeWithoutLeakingPaths(t *testing.T) {
	err := &os.PathError{Op: "open", Path: filepath.Join(t.TempDir(), "private-installation", "deployment.json"), Err: os.ErrNotExist}
	for _, jsonOutput := range []bool{false, true} {
		args := []string{"status", "--redact"}
		if jsonOutput {
			args = append(args, "--json")
		}
		var stdout, stderr bytes.Buffer
		writeFailure(&stdout, &stderr, args, err)
		combined := stdout.String() + stderr.String()
		if strings.Contains(combined, err.Path) || strings.Contains(combined, "private-installation") {
			t.Fatal("preflight leaked an installation path")
		}
		if jsonOutput {
			if stderr.Len() != 0 {
				t.Fatalf("json preflight error polluted stderr: %s", stderr.String())
			}
			var result struct {
				SchemaVersion string
				Ok            bool
				Data          any
				Error         *struct {
					Code, Message string
					ExitCode      int
				}
			}
			if parseErr := json.Unmarshal(stdout.Bytes(), &result); parseErr != nil {
				t.Fatal(parseErr)
			}
			if result.SchemaVersion != "1.0.0" || result.Ok || result.Data != nil || result.Error == nil || result.Error.Code != "launcher-state-invalid" || result.Error.ExitCode != 74 || result.Error.Message == "" {
				t.Fatalf("invalid error envelope: %s", stdout.String())
			}
		} else {
			if stdout.Len() != 0 {
				t.Fatalf("text preflight error wrote to stdout: %s", stdout.String())
			}
			if !strings.HasPrefix(stderr.String(), "[diag-code:launcher-state-invalid]") {
				t.Fatal("missing text error code")
			}
		}
	}
}

func TestHelperFailureCodesProduceActionableMessages(t *testing.T) {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("GO_LAUNCHER_HELPER_OUTPUT", filepath.Join(t.TempDir(), "helper.json"))
	for code, hint := range map[int]string{73: "in progress", 74: "recover", 75: "permissions"} {
		t.Setenv("GO_LAUNCHER_HELPER_EXIT", strconv.Itoa(code))
		failure := failureFor(exec.Command(self).Run())
		if failure.exitCode != code || !strings.Contains(failure.message, hint) || strings.Contains(failure.message, "exit status") {
			t.Fatalf("unhelpful helper failure: %+v", failure)
		}
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

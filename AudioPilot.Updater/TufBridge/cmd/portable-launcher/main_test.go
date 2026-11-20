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
			cwdOutput := filepath.Join(root, "cwd.txt")
			t.Setenv("GO_LAUNCHER_HELPER_OUTPUT", output)
			t.Setenv("GO_LAUNCHER_HELPER_CWD", cwdOutput)
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
			cwd, err := os.ReadFile(cwdOutput)
			if err != nil {
				t.Fatal(err)
			}
			expectedDirectory := folder
			if cli {
				expectedDirectory, err = os.Getwd()
				if err != nil {
					t.Fatal(err)
				}
			}
			if string(cwd) != expectedDirectory {
				t.Fatalf("working directory changed: got %s, want %s", cwd, expectedDirectory)
			}
			// During pending health, safe managed CLI controls remain reachable through the previous generation.
			if cli {
				pending := []byte(`{"Schema":1,"Current":"1.0.1_1123456789abcdef0123456789abcdef","Previous":"` + generation + `","PendingHealth":true}`)
				if err = os.WriteFile(filepath.Join(workspace, "deployment.json"), pending, 0600); err != nil {
					t.Fatal(err)
				}
				err = run(filepath.Join(root, name), []string{"update", "status", "--json"})
				if !errors.As(err, &exit) || exit.ExitCode() != 7 {
					t.Fatalf("safe recovery CLI could not start: %v", err)
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
		var output bytes.Buffer
		writeFailure(&output, args, err)
		if strings.Contains(output.String(), err.Path) || strings.Contains(output.String(), "private-installation") {
			t.Fatal("preflight leaked an installation path")
		}
		if jsonOutput {
			var result struct {
				SchemaVersion string
				Data          struct {
					Error struct {
						Code, Message string
						ExitCode      int
					}
				}
			}
			if parseErr := json.Unmarshal(output.Bytes(), &result); parseErr != nil {
				t.Fatal(parseErr)
			}
			if result.SchemaVersion != "1.0.0" || result.Data.Error.Code != "launcher-state-invalid" || result.Data.Error.ExitCode != 74 || result.Data.Error.Message == "" {
				t.Fatalf("invalid error envelope: %s", output.String())
			}
		} else if !strings.HasPrefix(output.String(), "[diag-code:launcher-state-invalid]") {
			t.Fatal("missing text error code")
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

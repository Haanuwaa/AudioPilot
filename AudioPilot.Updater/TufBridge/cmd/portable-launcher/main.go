package main

import (
	"bytes"
	"encoding/json"
	"errors"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"

	"audiopilot.local/tufbridge/internal/pathguard"
)

type state struct {
	Schema        int
	Current       *string
	Previous      *string
	PendingHealth bool
}

// Set at link time only by packaged portable builds (scripts/build-tuf-bridge.ps1).
var packagedLauncher string

type portableLayout struct {
	Schema       int
	DiagnosticId string
	Managed      bool
}

type recoveryRecord struct {
	Schema       int
	DiagnosticId string
	DataRoot     string
	Snapshot     string
	PreviousApp  string
	Revision     int64
	Completed    bool
}

var (
	generation   = regexp.MustCompile(`^[0-9]+\.[0-9]+\.[0-9]+_[a-f0-9]{32}$`)
	diagnosticID = regexp.MustCompile(`^[a-fA-F0-9]{32}$`)
	mirroredDocs = []string{
		"ABOUT.txt",
		filepath.Join("licenses", "LICENSE"),
		filepath.Join("licenses", "THIRD-PARTY-NOTICES.txt"),
		filepath.Join("licenses", "UPDATER-NOTICES.txt"),
	}
)

func readState(data []byte) (state, error) {
	var result state
	if len(data) > 8192 {
		return result, errors.New("oversized portable state")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	start, err := decoder.Token()
	if err != nil || start != json.Delim('{') {
		return result, errors.New("invalid portable state")
	}
	seen := map[string]bool{}
	for decoder.More() {
		key, err := decoder.Token()
		if err != nil {
			return result, err
		}
		name, ok := key.(string)
		if !ok || seen[name] || (name != "Schema" && name != "Current" && name != "Previous" && name != "PendingHealth") {
			return result, errors.New("ambiguous portable state")
		}
		seen[name] = true
		var value json.RawMessage
		if err = decoder.Decode(&value); err != nil {
			return result, err
		}
		if name == "PendingHealth" && !bytes.Equal(bytes.TrimSpace(value), []byte("true")) && !bytes.Equal(bytes.TrimSpace(value), []byte("false")) {
			return result, errors.New("invalid portable health state")
		}
	}
	if _, err = decoder.Token(); err != nil {
		return result, err
	}
	if _, err = decoder.Token(); err != io.EOF || len(seen) != 4 {
		return result, errors.New("incomplete or trailing portable state")
	}
	if err = json.Unmarshal(data, &result); err != nil {
		return result, err
	}
	if result.Schema != 1 || result.Current == nil || !generation.MatchString(*result.Current) || result.Previous != nil && !generation.MatchString(*result.Previous) || result.PendingHealth && result.Previous == nil {
		return result, errors.New("unsafe portable generation")
	}
	return result, nil
}

func run(executable string, args []string, checkElevation func() error) error {
	root := filepath.Join(filepath.Dir(executable), "app")
	if err := pathguard.Check(root); err != nil {
		return err
	}
	markerPath := filepath.Join(root, ".updater")
	if err := pathguard.Check(markerPath); err != nil {
		return err
	}
	markerFile, err := os.Open(markerPath)
	if err != nil {
		return err
	}
	marker, err := io.ReadAll(io.LimitReader(markerFile, 65))
	_ = markerFile.Close()
	if err != nil || string(marker) != "AudioPilot updater workspace 1" {
		return errors.New("this is not an owned portable installation")
	}
	path := filepath.Join(root, "deployment.json")
	if err = pathguard.Check(path); err != nil {
		return err
	}
	input, err := os.Open(path)
	if err != nil {
		return err
	}
	data, err := io.ReadAll(io.LimitReader(input, 8193))
	_ = input.Close()
	if err != nil {
		return err
	}
	current, err := readState(data)
	if err != nil {
		return err
	}
	selected := *current.Current
	if current.PendingHealth {
		selected = *current.Previous
	}
	folder := filepath.Join(root, "versions", selected)
	name := "AudioPilot.Updater.exe"
	arguments := append([]string{"pilot-launch", root}, args...)
	cli := strings.EqualFold(filepath.Base(executable), "AudioPilot.Cli.exe")
	if cli {
		name, arguments = "AudioPilot.Cli.exe", args
	} else {
		if err := checkElevation(); err != nil {
			return err
		}
		if !current.PendingHealth {
			if launched, fastErr := tryFastLaunchManagedGui(filepath.Dir(executable), root, folder, selected, args); launched {
				return fastErr
			}
		}
	}
	target := filepath.Join(folder, name)
	if err = pathguard.Check(target); err != nil {
		return err
	}
	command := exec.Command(target, arguments...)
	if !cli {
		command.Dir = folder
	}
	command.Stdin, command.Stdout, command.Stderr = os.Stdin, os.Stdout, os.Stderr
	return command.Run()
}

func tryFastLaunchManagedGui(portableRoot, workspace, folder, selected string, args []string) (bool, error) {
	if packagedLauncher != "1" {
		return false, nil
	}
	releaseLock, ok := tryAcquireExclusiveLock(filepath.Join(workspace, "lifecycle.lock"))
	if !ok {
		return false, nil
	}
	defer releaseLock()

	// Re-read deployment state under lifecycle.lock to eliminate TOCTOU with concurrent updates.
	refreshed, err := readDeploymentFile(filepath.Join(workspace, "deployment.json"))
	if err != nil || refreshed.PendingHealth || refreshed.Current == nil || *refreshed.Current != selected {
		return false, nil
	}
	layout, ok := readStrictManagedLayout(filepath.Join(workspace, "portable-layout.json"))
	if !ok {
		return false, nil
	}
	if hasPendingRecovery(workspace, layout.DiagnosticId) || !documentsSynchronized(portableRoot, workspace, selected) {
		return false, nil
	}
	target := filepath.Join(folder, "AudioPilot.exe")
	if err := pathguard.Check(target); err != nil {
		return false, nil
	}
	info, err := os.Stat(target)
	if err != nil || !info.Mode().IsRegular() {
		return false, nil
	}
	command := exec.Command(target, args...)
	command.Dir = folder
	command.Env = strippedAppEnvironment()
	if err := command.Start(); err != nil {
		return false, nil
	}
	_ = command.Process.Release()
	return true, nil
}

func readDeploymentFile(path string) (state, error) {
	var empty state
	if err := pathguard.Check(path); err != nil {
		return empty, err
	}
	input, err := os.Open(path)
	if err != nil {
		return empty, err
	}
	data, err := io.ReadAll(io.LimitReader(input, 8193))
	_ = input.Close()
	if err != nil {
		return empty, err
	}
	return readState(data)
}

func readStrictManagedLayout(path string) (portableLayout, bool) {
	var layout portableLayout
	if err := pathguard.Check(path); err != nil {
		return layout, false
	}
	file, err := os.Open(path)
	if err != nil {
		return layout, false
	}
	data, err := io.ReadAll(io.LimitReader(file, 4097))
	_ = file.Close()
	if err != nil || len(data) > 4096 {
		return layout, false
	}
	if !validateStrictJSONObject(data, map[string]bool{"Schema": true, "DiagnosticId": true, "Managed": true}, true) {
		return layout, false
	}
	if err := json.Unmarshal(data, &layout); err != nil {
		return layout, false
	}
	valid := layout.Schema == 1 && layout.Managed &&
		diagnosticID.MatchString(layout.DiagnosticId) &&
		strings.Trim(layout.DiagnosticId, "0") != ""
	return layout, valid
}

func hasPendingRecovery(workspace, expectedDiagnosticID string) bool {
	newPath := filepath.Join(workspace, "recovery.json.new")
	if _, err := os.Lstat(newPath); err == nil || !errors.Is(err, os.ErrNotExist) {
		return true
	}
	recPath := filepath.Join(workspace, "recovery.json")
	if _, err := os.Lstat(recPath); errors.Is(err, os.ErrNotExist) {
		return false
	} else if err != nil {
		return true
	}
	if err := pathguard.Check(recPath); err != nil {
		return true
	}
	file, err := os.Open(recPath)
	if err != nil {
		return true
	}
	data, err := io.ReadAll(io.LimitReader(file, 8193))
	_ = file.Close()
	if err != nil || len(data) > 8192 {
		return true
	}
	if !validateStrictJSONObject(data, nil, false) {
		return true
	}
	var rec recoveryRecord
	if err := json.Unmarshal(data, &rec); err != nil {
		return true
	}
	if rec.Schema != 1 || !rec.Completed || rec.Revision <= 0 ||
		rec.DiagnosticId != expectedDiagnosticID ||
		!diagnosticID.MatchString(rec.Snapshot) ||
		!filepath.IsAbs(rec.DataRoot) || !filepath.IsAbs(rec.PreviousApp) {
		return true
	}
	return false
}

func validateStrictJSONObject(data []byte, allowedKeys map[string]bool, requireAll bool) bool {
	decoder := json.NewDecoder(bytes.NewReader(data))
	start, err := decoder.Token()
	if err != nil || start != json.Delim('{') {
		return false
	}
	seen := map[string]bool{}
	for decoder.More() {
		key, err := decoder.Token()
		if err != nil {
			return false
		}
		name, ok := key.(string)
		if !ok || seen[name] || (allowedKeys != nil && !allowedKeys[name]) {
			return false
		}
		seen[name] = true
		var raw json.RawMessage
		if err := decoder.Decode(&raw); err != nil {
			return false
		}
	}
	if _, err := decoder.Token(); err != nil {
		return false
	}
	if _, err := decoder.Token(); err != io.EOF {
		return false
	}
	return !requireAll || len(seen) == len(allowedKeys)
}

func documentsSynchronized(portableRoot, workspace, selected string) bool {
	receiptPath := filepath.Join(workspace, ".documents-generation")
	if err := pathguard.Check(receiptPath); err != nil {
		return false
	}
	file, err := os.Open(receiptPath)
	if err != nil {
		return false
	}
	data, err := io.ReadAll(io.LimitReader(file, 101))
	_ = file.Close()
	if err != nil || len(data) > 100 || string(data) != selected {
		return false
	}
	for _, rel := range mirroredDocs {
		docPath := filepath.Join(portableRoot, rel)
		if err := pathguard.Check(docPath); err != nil {
			return false
		}
		if info, err := os.Stat(docPath); err != nil || !info.Mode().IsRegular() {
			return false
		}
	}
	return true
}

func strippedAppEnvironment() []string {
	env := os.Environ()
	filtered := make([]string, 0, len(env))
	for _, entry := range env {
		key, _, _ := strings.Cut(entry, "=")
		if strings.EqualFold(key, "AUDIOPILOT_DIAGNOSTIC_SESSION") ||
			strings.EqualFold(key, "AUDIOPILOT_UPDATE_TEST_SESSION") ||
			strings.EqualFold(key, "AUDIOPILOT_UPDATE_LIFECYCLE") {
			continue
		}
		filtered = append(filtered, entry)
	}
	return filtered
}

func main() {
	executable, err := os.Executable()
	cli := isCliExecutableName(executable) || (len(os.Args) > 0 && isCliExecutableName(os.Args[0]))
	if err == nil {
		err = run(executable, os.Args[1:], requireUnelevated)
	}
	if err != nil {
		var exit *exec.ExitError
		code := failureFor(err).exitCode
		if errors.As(err, &exit) {
			code = exit.ExitCode()
		}
		if exit == nil || !cli {
			showError(err, cli)
		}
		os.Exit(code)
	}
}

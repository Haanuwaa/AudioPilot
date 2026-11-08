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

var generation = regexp.MustCompile(`^[0-9]+\.[0-9]+\.[0-9]+_[a-f0-9]{32}$`)

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

func run(executable string, args []string) error {
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
		if current.PendingHealth {
			return errors.New("open AudioPilot.exe to recover the interrupted update first")
		}
		name, arguments = "AudioPilot.Cli.exe", args
	}
	target := filepath.Join(folder, name)
	if err = pathguard.Check(target); err != nil {
		return err
	}
	command := exec.Command(target, arguments...)
	command.Dir = folder
	command.Stdin, command.Stdout, command.Stderr = os.Stdin, os.Stdout, os.Stderr
	return command.Run()
}

func main() {
	executable, err := os.Executable()
	if err == nil {
		err = run(executable, os.Args[1:])
	}
	if err != nil {
		var exit *exec.ExitError
		code := 1
		if errors.As(err, &exit) {
			code = exit.ExitCode()
		}
		if exit == nil || !strings.EqualFold(filepath.Base(executable), "AudioPilot.Cli.exe") {
			showError(err)
		}
		os.Exit(code)
	}
}

package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
)

// These exit codes form the small contract with PortableLaunchFailure in UpdateProtocol.
type launchFailure struct {
	code, message string
	exitCode      int
}

func failureFor(err error) launchFailure {
	failure := launchFailure{"launcher-state-invalid", "AudioPilot files or update state could not be read. Close AudioPilot and reopen it to recover. If this continues, extract or reinstall a complete copy.", 74}
	var child *exec.ExitError
	if errors.As(err, &child) {
		failure = launchFailure{"launcher-start-failed", "AudioPilot could not finish starting. Try again. If this continues, extract or reinstall a complete copy.", child.ExitCode()}
		switch child.ExitCode() {
		case 73:
			failure.code, failure.message = "launcher-busy", "Another AudioPilot launch or update is in progress. Wait for it to finish, then try again."
		case 74:
			failure.code, failure.message = "launcher-state-invalid", "AudioPilot files or update state are missing or invalid. Reopen AudioPilot to recover, or extract or reinstall a complete copy."
		case 75:
			failure.code, failure.message = "launcher-access-denied", "AudioPilot could not access its files. Check the folder permissions, then try again."
		}
	} else if errors.Is(err, os.ErrPermission) {
		failure = launchFailure{"launcher-access-denied", "AudioPilot could not access its files. Check the folder permissions, then try again.", 75}
	} else if errors.Is(err, os.ErrNotExist) {
		failure.message = "AudioPilot files are missing. Extract or reinstall a complete copy."
	}
	return failure
}

func writeFailure(output io.Writer, args []string, err error) {
	failure := failureFor(err)
	for _, argument := range args {
		if strings.EqualFold(strings.TrimSpace(argument), "--json") {
			// Match the managed CLI envelope. Native preflight never includes paths or raw exception text.
			_ = json.NewEncoder(output).Encode(map[string]any{"schemaVersion": "1.0.0", "data": map[string]any{"error": map[string]any{
				"code": failure.code, "message": failure.message, "exitCode": failure.exitCode,
			}}})
			return
		}
	}
	fmt.Fprintf(output, "[diag-code:%s] %s\n", failure.code, failure.message)
}

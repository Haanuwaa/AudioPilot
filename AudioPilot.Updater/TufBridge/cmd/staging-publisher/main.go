// staging-publisher is an operator tool, never shipped with the updater client.
package main

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"os"

	"audiopilot.local/tufbridge/internal/staging"
)

func main() {
	if len(os.Args) == 2 && os.Args[1] == "--help" {
		fmt.Println("AudioPilot staging authority (operator only). JSON request on stdin; JSON result on stdout.\nOperations: initialize, publish, renew, check, backup, restore, rotate.\nUse scripts/publish-update-staging.ps1 for protected key storage and secure password prompts.\nRenew with snapshot/timestamp keys only preserves the approved targets bytes.\ncheck supports metadataOnly for on-demand feed health. No operation enables public live updates.\nNever pass passwords or private keys as arguments. Rotate builds a fresh verified destination before authority selection.")
		return
	}
	if len(os.Args) != 1 {
		fmt.Fprintln(os.Stderr, "use --help or send one JSON request on stdin")
		os.Exit(2)
	}
	if err := run(os.Stdin, os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}

func run(input io.Reader, output io.Writer) error {
	data, err := io.ReadAll(io.LimitReader(input, 65537))
	if err != nil {
		return err
	}
	if len(data) > 65536 {
		return fmt.Errorf("oversized publisher input")
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.DisallowUnknownFields()
	var request staging.Request
	if err := decoder.Decode(&request); err != nil {
		return err
	}
	if decoder.Decode(new(any)) != io.EOF {
		return fmt.Errorf("trailing publisher input")
	}
	result, err := staging.Execute(request)
	if err != nil {
		return err
	}
	return json.NewEncoder(output).Encode(result)
}

//go:build !windows

package main

import (
	"os"
)

func showError(err error, _ bool) { writeFailure(os.Stdout, os.Stderr, os.Args[1:], err) }

func requireUnelevated() error { return nil }

//go:build !windows

package main

import (
	"os"
)

func showError(err error) { writeFailure(os.Stderr, os.Args[1:], err) }

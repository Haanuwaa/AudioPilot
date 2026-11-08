package main

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

func showError(err error) {
	if strings.EqualFold(filepath.Base(os.Args[0]), "AudioPilot.Cli.exe") {
		fmt.Fprintln(os.Stderr, "AudioPilot:", err)
		return
	}
	text, _ := windows.UTF16PtrFromString("AudioPilot could not start.\n\n" + err.Error())
	title, _ := windows.UTF16PtrFromString("AudioPilot")
	_, _, _ = windows.NewLazySystemDLL("user32.dll").NewProc("MessageBoxW").Call(0, uintptr(unsafe.Pointer(text)), uintptr(unsafe.Pointer(title)), 0x10)
}

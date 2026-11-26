package main

import (
	"os"
	"unsafe"

	"golang.org/x/sys/windows"
)

func showError(err error, cli bool) {
	if cli || (len(os.Args) > 0 && isCliExecutableName(os.Args[0])) {
		writeFailure(os.Stdout, os.Stderr, os.Args[1:], err)
		return
	}
	text, _ := windows.UTF16PtrFromString("AudioPilot could not start.\n\n" + failureFor(err).message)
	title, _ := windows.UTF16PtrFromString("AudioPilot")
	_, _, _ = windows.NewLazySystemDLL("user32.dll").NewProc("MessageBoxW").Call(0, uintptr(unsafe.Pointer(text)), uintptr(unsafe.Pointer(title)), 0x10)
}

func requireUnelevated() error {
	var elevated, count uint32
	if err := windows.GetTokenInformation(windows.GetCurrentProcessToken(), windows.TokenElevation,
		(*byte)(unsafe.Pointer(&elevated)), uint32(unsafe.Sizeof(elevated)), &count); err != nil {
		return err
	}
	if elevated != 0 {
		return errElevated
	}
	return nil
}

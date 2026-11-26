//go:build windows

package main

import (
	"audiopilot.local/tufbridge/internal/pathguard"
	"golang.org/x/sys/windows"
)

func tryAcquireExclusiveLock(path string) (func(), bool) {
	if err := pathguard.Check(path); err != nil {
		return nil, false
	}
	utf16Path, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, false
	}
	handle, err := windows.CreateFile(
		utf16Path,
		windows.GENERIC_READ|windows.GENERIC_WRITE,
		0,
		nil,
		windows.OPEN_ALWAYS,
		windows.FILE_ATTRIBUTE_NORMAL|windows.FILE_FLAG_OPEN_REPARSE_POINT,
		0,
	)
	if err != nil {
		return nil, false
	}
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(handle, &info); err != nil ||
		(info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT) != 0 ||
		(info.FileAttributes&windows.FILE_ATTRIBUTE_DIRECTORY) != 0 {
		_ = windows.CloseHandle(handle)
		return nil, false
	}
	return func() { _ = windows.CloseHandle(handle) }, true
}

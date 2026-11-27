package main

import (
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	advapi32DLL     = windows.NewLazySystemDLL("advapi32.dll")
	getUserNameProc = advapi32DLL.NewProc("GetUserNameW")
	currentUserName = readWindowsUserName
)

func readWindowsUserName() string {
	buffer := make([]uint16, 256)
	size := uint32(len(buffer))
	r1, _, _ := getUserNameProc.Call(uintptr(unsafe.Pointer(&buffer[0])), uintptr(unsafe.Pointer(&size)))
	if r1 == 0 || size == 0 {
		return ""
	}
	return syscall.UTF16ToString(buffer[:size])
}

func isWindowsSandboxAccount() bool {
	return currentUserName() == "WDAGUtilityAccount"
}

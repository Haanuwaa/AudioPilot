package pathguard

import (
	"errors"
	"os"
	"syscall"
)

func isRedirected(_ string, info os.FileInfo) (bool, error) {
	attributes, ok := info.Sys().(*syscall.Win32FileAttributeData)
	if !ok {
		return false, errors.New("Windows file attributes unavailable")
	}
	return attributes.FileAttributes&syscall.FILE_ATTRIBUTE_REPARSE_POINT != 0, nil
}

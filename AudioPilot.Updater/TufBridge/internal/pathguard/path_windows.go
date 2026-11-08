package pathguard

import (
	"os"

	"golang.org/x/sys/windows"
)

func isRedirected(path string, _ os.FileInfo) (bool, error) {
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return false, err
	}
	attributes, err := windows.GetFileAttributes(name)
	return attributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0, err
}

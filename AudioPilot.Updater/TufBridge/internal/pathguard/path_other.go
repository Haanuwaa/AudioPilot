//go:build !windows

package pathguard

import "os"

func isRedirected(_ string, info os.FileInfo) (bool, error) {
	return info.Mode()&os.ModeSymlink != 0, nil
}

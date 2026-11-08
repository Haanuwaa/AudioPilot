// Package pathguard rejects redirected ancestors while accepting aliases of the same filesystem object.
package pathguard

import (
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
)

func Check(path string) error {
	if !filepath.IsAbs(path) {
		return errors.New("path must be absolute")
	}
	for current := filepath.Clean(path); ; current = filepath.Dir(current) {
		info, err := os.Lstat(current)
		if err == nil {
			redirected, err := isRedirected(current, info)
			if err != nil {
				return err
			}
			if redirected {
				return errors.New("redirected path")
			}
			// Windows attributes reject every reparse point without resolving all ancestors again.
			if runtime.GOOS != "windows" {
				real, err := filepath.EvalSymlinks(current)
				if err != nil {
					return err
				}
				if !strings.EqualFold(filepath.Clean(real), current) {
					resolved, err := os.Lstat(real)
					if err != nil || !os.SameFile(info, resolved) {
						return errors.New("redirected path ancestor")
					}
				}
			}
		} else if !os.IsNotExist(err) {
			return err
		}
		if filepath.Dir(current) == current {
			return nil
		}
	}
}

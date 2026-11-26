//go:build !windows

package main

func tryAcquireExclusiveLock(_ string) (func(), bool) {
	return nil, false
}

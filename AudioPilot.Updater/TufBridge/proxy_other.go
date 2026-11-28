//go:build !windows

package main

import (
	"net/http"
	"net/url"
)

func resolveStagingProxy(req *http.Request) (*url.URL, error) {
	return http.ProxyFromEnvironment(req)
}

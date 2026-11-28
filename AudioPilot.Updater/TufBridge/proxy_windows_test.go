//go:build windows

package main

import (
	"net/url"
	"testing"
)

func TestSelectWindowsProxyEntry(t *testing.T) {
	tests := []struct {
		name        string
		scheme      string
		proxyServer string
		want        string
	}{
		{name: "empty", scheme: "https", proxyServer: "   ", want: ""},
		{name: "single host without scheme", scheme: "https", proxyServer: "proxy.corp:8080", want: "proxy.corp:8080"},
		{name: "per-scheme exact https", scheme: "https", proxyServer: "http=http.corp:8080;https=https.corp:8443", want: "https.corp:8443"},
		{name: "per-scheme exact http", scheme: "http", proxyServer: "http=http.corp:8080;https=https.corp:8443", want: "http.corp:8080"},
		{name: "case insensitive scheme match", scheme: "HTTPS", proxyServer: "HTTPS=secure.corp:8443;HTTP=plain.corp:8080", want: "secure.corp:8443"},
		{name: "https falls back to http entry", scheme: "https", proxyServer: "http=fallback.corp:8080", want: "fallback.corp:8080"},
		{name: "falls back to socks with socks5 prefix", scheme: "https", proxyServer: "socks=127.0.0.1:1080", want: "socks5://127.0.0.1:1080"},
		{name: "preserves explicit socks scheme", scheme: "https", proxyServer: "socks=socks5h://127.0.0.1:1080", want: "socks5h://127.0.0.1:1080"},
		{name: "ignores blank entries", scheme: "https", proxyServer: " ; ; https=ok.corp:3128 ; ", want: "ok.corp:3128"},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			if got := selectWindowsProxyEntry(tc.scheme, tc.proxyServer); got != tc.want {
				t.Fatalf("selectWindowsProxyEntry(%q, %q) = %q, want %q", tc.scheme, tc.proxyServer, got, tc.want)
			}
		})
	}
}

func TestMatchesWindowsProxyBypass(t *testing.T) {
	tests := []struct {
		name     string
		rawURL   string
		override string
		want     bool
	}{
		{name: "empty override", rawURL: "https://updates.example.com/root.json", override: "", want: false},
		{name: "local matches loopback ipv4", rawURL: "http://127.0.0.1:8080/feed", override: "<local>", want: true},
		{name: "local matches loopback ipv6", rawURL: "http://[::1]:8080/feed", override: "<local>", want: true},
		{name: "local matches unqualified hostname", rawURL: "https://intranet/feed", override: "<local>", want: true},
		{name: "local does not match fqdn", rawURL: "https://updates.example.com/feed", override: "<local>", want: false},
		{name: "local does not match public ipv4", rawURL: "https://93.184.216.34/feed", override: "<local>", want: false},
		{name: "wildcard suffix matches subdomain", rawURL: "https://api.corp.internal/feed", override: "*.corp.internal", want: true},
		{name: "wildcard suffix case insensitive", rawURL: "https://API.CORP.INTERNAL/feed", override: "*.corp.internal", want: true},
		{name: "scheme-scoped rule matches matching scheme", rawURL: "https://cdn.example.com/file", override: "https://*.example.com", want: true},
		{name: "scheme-scoped rule ignores different scheme", rawURL: "http://cdn.example.com/file", override: "https://*.example.com", want: false},
		{name: "port-scoped rule matches explicit port", rawURL: "https://updates.example.com:8443/file", override: "updates.example.com:8443", want: true},
		{name: "port-scoped rule matches default https port", rawURL: "https://updates.example.com/file", override: "updates.example.com:443", want: true},
		{name: "port-scoped rule rejects mismatched port", rawURL: "https://updates.example.com:8443/file", override: "updates.example.com:443", want: false},
		{name: "multi-rule list matches second entry", rawURL: "https://mirror.local.test/file", override: "other.test;*.local.test;<local>", want: true},
	}

	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			u, err := url.Parse(tc.rawURL)
			if err != nil {
				t.Fatalf("url.Parse(%q) failed: %v", tc.rawURL, err)
			}
			if got := matchesWindowsProxyBypass(u, tc.override); got != tc.want {
				t.Fatalf("matchesWindowsProxyBypass(%q, %q) = %v, want %v", tc.rawURL, tc.override, got, tc.want)
			}
		})
	}
}

func TestParseWindowsProxyConfig(t *testing.T) {
	target, _ := url.Parse("https://updates.example.com/targets.json")

	if got, err := parseWindowsProxyConfig(target, false, "proxy.corp:8080", ""); err != nil || got != nil {
		t.Fatalf("disabled proxy should return nil, got %v (err=%v)", got, err)
	}
	if got, err := parseWindowsProxyConfig(target, true, "proxy.corp:8080", "*.example.com"); err != nil || got != nil {
		t.Fatalf("bypassed host should return nil, got %v (err=%v)", got, err)
	}
	got, err := parseWindowsProxyConfig(target, true, "http=http.corp:8080;https=https.corp:8443", "<local>")
	if err != nil || got == nil || got.String() != "http://https.corp:8443" {
		t.Fatalf("unexpected parsed proxy: %v (err=%v)", got, err)
	}
	socksGot, err := parseWindowsProxyConfig(target, true, "socks=127.0.0.1:1080", "")
	if err != nil || socksGot == nil || socksGot.String() != "socks5://127.0.0.1:1080" {
		t.Fatalf("unexpected parsed socks proxy: %v (err=%v)", socksGot, err)
	}
	if invalidGot, err := parseWindowsProxyConfig(target, true, "http://", ""); err != nil || invalidGot != nil {
		t.Fatalf("empty-host proxy should return nil, got %v (err=%v)", invalidGot, err)
	}
}

func TestWildcardMatchFold(t *testing.T) {
	if !wildcardMatchFold("*", "anything.example.com") {
		t.Fatal("single wildcard should match anything")
	}
	if !wildcardMatchFold("a*b*c", "a123b456c") {
		t.Fatal("multi-wildcard pattern should match")
	}
	if wildcardMatchFold("a*b*c", "a123b456d") {
		t.Fatal("suffix mismatch should fail")
	}
	if !wildcardMatchFold("exact.host", "exact.host") {
		t.Fatal("exact match should succeed")
	}
	if wildcardMatchFold("exact.host", "other.host") {
		t.Fatal("different exact host should fail")
	}
}

func TestHasEnvProxyConfig(t *testing.T) {
	for _, key := range []string{"HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "NO_PROXY", "no_proxy"} {
		t.Setenv(key, "")
	}
	if hasEnvProxyConfig() {
		t.Fatal("expected false when all proxy env vars are empty")
	}
	t.Setenv("HTTPS_PROXY", "http://127.0.0.1:8888")
	if !hasEnvProxyConfig() {
		t.Fatal("expected true when HTTPS_PROXY is set")
	}
}

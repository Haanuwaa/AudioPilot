//go:build windows

package main

import (
	"net"
	"net/http"
	"net/url"
	"os"
	"strings"

	"golang.org/x/sys/windows/registry"
)

func resolveStagingProxy(req *http.Request) (*url.URL, error) {
	if hasEnvProxyConfig() {
		return http.ProxyFromEnvironment(req)
	}
	return resolveWindowsSystemProxy(req.URL)
}

func hasEnvProxyConfig() bool {
	for _, key := range []string{"HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "NO_PROXY", "no_proxy"} {
		if strings.TrimSpace(os.Getenv(key)) != "" {
			return true
		}
	}
	return false
}

func resolveWindowsSystemProxy(target *url.URL) (*url.URL, error) {
	key, err := registry.OpenKey(registry.CURRENT_USER, `Software\Microsoft\Windows\CurrentVersion\Internet Settings`, registry.QUERY_VALUE)
	if err != nil {
		return nil, nil
	}
	defer key.Close()

	enabled, _, err := key.GetIntegerValue("ProxyEnable")
	if err != nil || enabled == 0 {
		return nil, nil
	}
	server, _, err := key.GetStringValue("ProxyServer")
	if err != nil || strings.TrimSpace(server) == "" {
		return nil, nil
	}
	override, _, _ := key.GetStringValue("ProxyOverride")
	return parseWindowsProxyConfig(target, true, server, override)
}

func parseWindowsProxyConfig(target *url.URL, enabled bool, proxyServer, proxyOverride string) (*url.URL, error) {
	if !enabled || target == nil {
		return nil, nil
	}
	if matchesWindowsProxyBypass(target, proxyOverride) {
		return nil, nil
	}
	raw := selectWindowsProxyEntry(target.Scheme, proxyServer)
	if raw == "" {
		return nil, nil
	}
	if !strings.Contains(raw, "://") {
		raw = "http://" + raw
	}
	parsed, err := url.Parse(raw)
	if err != nil || parsed.Host == "" {
		return nil, nil
	}
	return parsed, nil
}

func selectWindowsProxyEntry(scheme, proxyServer string) string {
	trimmed := strings.TrimSpace(proxyServer)
	if trimmed == "" {
		return ""
	}
	if !strings.Contains(trimmed, "=") {
		return trimmed
	}
	wanted := strings.ToLower(scheme)
	var fallback string
	var socksFallback string
	for _, entry := range strings.Split(trimmed, ";") {
		entry = strings.TrimSpace(entry)
		if entry == "" {
			continue
		}
		k, v, hasEq := strings.Cut(entry, "=")
		if !hasEq {
			if fallback == "" {
				fallback = entry
			}
			continue
		}
		k = strings.ToLower(strings.TrimSpace(k))
		v = strings.TrimSpace(v)
		if v == "" {
			continue
		}
		if k == wanted {
			return v
		}
		if k == "http" && fallback == "" {
			fallback = v
		} else if k == "socks" && socksFallback == "" {
			if strings.Contains(v, "://") {
				socksFallback = v
			} else {
				socksFallback = "socks5://" + v
			}
		}
	}
	if fallback != "" {
		return fallback
	}
	return socksFallback
}

func matchesWindowsProxyBypass(target *url.URL, proxyOverride string) bool {
	if strings.TrimSpace(proxyOverride) == "" || target == nil {
		return false
	}
	scheme := strings.ToLower(target.Scheme)
	host := strings.ToLower(target.Hostname())
	hostPort := strings.ToLower(target.Host)
	if target.Port() == "" {
		switch scheme {
		case "https":
			hostPort = net.JoinHostPort(host, "443")
		case "http":
			hostPort = net.JoinHostPort(host, "80")
		}
	}
	for _, rule := range strings.Split(proxyOverride, ";") {
		rule = strings.ToLower(strings.TrimSpace(rule))
		if rule == "" {
			continue
		}
		if rule == "<local>" {
			if ip := net.ParseIP(host); ip != nil {
				if ip.IsLoopback() {
					return true
				}
			} else if !strings.Contains(host, ".") {
				return true
			}
			continue
		}
		if idx := strings.Index(rule, "://"); idx >= 0 {
			if rule[:idx] != scheme {
				continue
			}
			rule = rule[idx+3:]
		}
		candidate := host
		if strings.Contains(rule, ":") {
			candidate = hostPort
		}
		if wildcardMatchFold(rule, candidate) {
			return true
		}
	}
	return false
}

func wildcardMatchFold(pattern, value string) bool {
	if pattern == "*" {
		return true
	}
	parts := strings.Split(pattern, "*")
	if len(parts) == 1 {
		return pattern == value
	}
	if !strings.HasPrefix(value, parts[0]) {
		return false
	}
	rest := value[len(parts[0]):]
	for i := 1; i < len(parts)-1; i++ {
		if parts[i] == "" {
			continue
		}
		idx := strings.Index(rest, parts[i])
		if idx < 0 {
			return false
		}
		rest = rest[idx+len(parts[i]):]
	}
	return strings.HasSuffix(rest, parts[len(parts)-1])
}

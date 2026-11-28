package main

import (
	"crypto/tls"
	"net/http"
	"net/url"
	"testing"
)

func TestStagingTransportIsSandboxBoundAndUsesNormalTLS(t *testing.T) {
	req := request{Origin: stagingOrigin, BootstrapHash: stagingRoot}
	previousUser := currentUserName
	t.Cleanup(func() { currentUserName = previousUser })
	currentUserName = func() string { return "ordinary-user" }
	t.Setenv("USERNAME", "WDAGUtilityAccount")
	if _, err := newStagingFetcher(req); err == nil {
		t.Fatal("ordinary process opened staging")
	}
	currentUserName = func() string { return "WDAGUtilityAccount" }
	f, err := newStagingFetcher(req)
	if err != nil {
		t.Fatal(err)
	}
	defer f.client.CloseIdleConnections()
	transport := f.client.Transport.(*http.Transport)
	if transport.Proxy == nil {
		t.Fatal("staging transport disabled environment proxy support")
	}
	if transport.TLSClientConfig.InsecureSkipVerify || transport.TLSClientConfig.RootCAs != nil || transport.TLSClientConfig.MinVersion < tls.VersionTLS12 {
		t.Fatal("staging weakened public TLS")
	}
	for _, change := range []func(*request){func(r *request) { r.Origin = "https://example.invalid/" }, func(r *request) { r.BootstrapHash = "wrong" }, func(r *request) { r.TLSRoot = "arbitrary.pem" }, func(r *request) { r.TLSRootHash = "arbitrary" }} {
		bad := req
		change(&bad)
		if _, err := newStagingFetcher(bad); err == nil {
			t.Fatal("staging accepted an origin or trust override")
		}
	}
	for _, location := range []string{"http://release-assets.githubusercontent.com/github-production-release-asset/file", "https://example.invalid/github-production-release-asset/file", "https://name:secret@release-assets.githubusercontent.com/github-production-release-asset/file", "https://release-assets.githubusercontent.com/elsewhere/file"} {
		u, _ := url.Parse(location)
		if err = f.client.CheckRedirect(&http.Request{URL: u}, []*http.Request{{}}); err == nil {
			t.Fatalf("unsafe redirect: %s", location)
		}
	}
	u, _ := url.Parse("https://release-assets.githubusercontent.com/github-production-release-asset/123/file?sp=r&sig=temporary")
	if err = f.client.CheckRedirect(&http.Request{URL: u}, []*http.Request{{}}); err != nil {
		t.Fatal(err)
	}
	if err = f.client.CheckRedirect(&http.Request{URL: u}, []*http.Request{{}, {}, {}}); err == nil {
		t.Fatal("unbounded redirects")
	}
}

func TestPilotTransportRequiresItsIsolatedBuildAndKeepsStagingBoundaries(t *testing.T) {
	previousUser := currentUserName
	prior := pilotEnabled
	t.Cleanup(func() { pilotEnabled = prior; currentUserName = previousUser })
	currentUserName = func() string { return "ordinary-user" }
	t.Setenv("USERNAME", "WDAGUtilityAccount")
	pilotEnabled = ""
	req := request{Origin: stagingOrigin, BootstrapHash: stagingRoot, Transport: "pilot"}
	if _, err := newStagingFetcher(req); err == nil {
		t.Fatal("ordinary client authorized pilot transport")
	}
	pilotEnabled = "1"
	f, err := newStagingFetcher(req)
	if err != nil {
		t.Fatal(err)
	}
	f.client.CloseIdleConnections()
	for _, change := range []func(*request){
		func(r *request) { r.Transport = "staging" },
		func(r *request) { r.Origin = "https://example.invalid/" },
		func(r *request) { r.BootstrapHash = "wrong" },
		func(r *request) { r.TLSRoot = "arbitrary.pem" },
	} {
		bad := req
		change(&bad)
		if _, err := newStagingFetcher(bad); err == nil {
			t.Fatal("pilot weakened a transport or trust boundary")
		}
	}
}

func TestWindowsProxyConfigParsingAndBypass(t *testing.T) {
	githubURL, _ := url.Parse("https://github.com/Haanuwaa/AudioPilot/releases/download/updater-stable-feed/timestamp.json")
	localURL, _ := url.Parse("https://intranet/timestamp.json")
	ipv6PublicURL, _ := url.Parse("https://[2606:50c0:8000::154]/timestamp.json")
	ipv6LoopbackURL, _ := url.Parse("https://[::1]/timestamp.json")

	if proxy, err := parseWindowsProxyConfig(githubURL, false, "proxy.corp:8080", ""); err != nil || proxy != nil {
		t.Fatalf("disabled proxy returned %v (%v)", proxy, err)
	}
	proxy, err := parseWindowsProxyConfig(githubURL, true, "proxy.corp:8080", "<local>;*.internal")
	if err != nil || proxy == nil || proxy.String() != "http://proxy.corp:8080" {
		t.Fatalf("unexpected single proxy result: %v (%v)", proxy, err)
	}
	proxy, err = parseWindowsProxyConfig(githubURL, true, "http=http-proxy:8080;https=https-proxy:8443", "")
	if err != nil || proxy == nil || proxy.String() != "http://https-proxy:8443" {
		t.Fatalf("unexpected scheme proxy result: %v (%v)", proxy, err)
	}
	proxy, err = parseWindowsProxyConfig(githubURL, true, "socks=127.0.0.1:1080", "")
	if err != nil || proxy == nil || proxy.String() != "socks5://127.0.0.1:1080" {
		t.Fatalf("unexpected socks proxy result: %v (%v)", proxy, err)
	}
	if proxy, err := parseWindowsProxyConfig(localURL, true, "proxy.corp:8080", "<local>"); err != nil || proxy != nil {
		t.Fatalf("<local> bypass was ignored: %v (%v)", proxy, err)
	}
	if proxy, err := parseWindowsProxyConfig(ipv6LoopbackURL, true, "proxy.corp:8080", "<local>"); err != nil || proxy != nil {
		t.Fatalf("<local> loopback IPv6 bypass was ignored: %v (%v)", proxy, err)
	}
	proxy, err = parseWindowsProxyConfig(ipv6PublicURL, true, "proxy.corp:8080", "<local>")
	if err != nil || proxy == nil || proxy.String() != "http://proxy.corp:8080" {
		t.Fatalf("<local> erroneously bypassed public IPv6 host: %v (%v)", proxy, err)
	}
	if proxy, err := parseWindowsProxyConfig(githubURL, true, "proxy.corp:8080", "*.github.com;github.com"); err != nil || proxy != nil {
		t.Fatalf("host bypass was ignored: %v (%v)", proxy, err)
	}
	if proxy, err := parseWindowsProxyConfig(githubURL, true, "proxy.corp:8080", "github.com:443"); err != nil || proxy != nil {
		t.Fatalf("explicit default port bypass was ignored: %v (%v)", proxy, err)
	}
	proxy, err = parseWindowsProxyConfig(githubURL, true, "proxy.corp:8080", "http://github.com")
	if err != nil || proxy == nil || proxy.String() != "http://proxy.corp:8080" {
		t.Fatalf("http-only bypass rule erroneously bypassed https: %v (%v)", proxy, err)
	}
	if proxy, err := parseWindowsProxyConfig(githubURL, true, "proxy.corp:8080", "https://github.com"); err != nil || proxy != nil {
		t.Fatalf("https bypass rule was ignored: %v (%v)", proxy, err)
	}

	t.Setenv("HTTPS_PROXY", "http://env-proxy:8080")
	t.Setenv("NO_PROXY", "github.com")
	req, _ := http.NewRequest(http.MethodGet, githubURL.String(), nil)
	if proxy, err := resolveStagingProxy(req); err != nil || proxy != nil {
		t.Fatalf("NO_PROXY was overridden by fallback: %v (%v)", proxy, err)
	}
}

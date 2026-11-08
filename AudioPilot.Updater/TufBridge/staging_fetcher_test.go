package main

import (
	"crypto/tls"
	"net/http"
	"net/url"
	"testing"
)

func TestStagingTransportIsSandboxBoundAndUsesNormalTLS(t *testing.T) {
	req := request{Origin: stagingOrigin, BootstrapHash: stagingRoot}
	t.Setenv("USERNAME", "ordinary-user")
	if _, err := newStagingFetcher(req); err == nil {
		t.Fatal("ordinary process opened staging")
	}
	t.Setenv("USERNAME", "WDAGUtilityAccount")
	f, err := newStagingFetcher(req)
	if err != nil {
		t.Fatal(err)
	}
	defer f.client.CloseIdleConnections()
	transport := f.client.Transport.(*http.Transport)
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
	t.Setenv("USERNAME", "ordinary-user")
	prior := pilotEnabled
	t.Cleanup(func() { pilotEnabled = prior })
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

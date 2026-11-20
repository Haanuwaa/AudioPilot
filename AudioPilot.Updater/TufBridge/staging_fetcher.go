package main

import (
	"crypto/tls"
	"errors"
	"io"
	"net/http"
	"net/url"
	"os"
	"regexp"
	"strings"
	"time"

	"github.com/theupdateframework/go-tuf/v2/metadata"
)

const stagingOrigin = "https://github.com/Haanuwaa/AudioPilot/releases/download/updater-staging-feed/"
const stagingRoot = "85D114D65F2367E8A587765BDF56580E08E9F4D0D644559C73B991209453F61F"

// Set only by the isolated opt-in pilot build; ordinary clients remain Sandbox-only.
var pilotEnabled string

// Bootstrap builds bind the independent root and explicit public activation at link time.
var stableRoot string
var stableEnabled string
var sandboxAcceptance string

const stableOrigin = "https://github.com/Haanuwaa/AudioPilot/releases/download/updater-stable-feed/"

var stagingMetadataName = regexp.MustCompile(`^([1-9][0-9]{0,6}\.(root|targets|snapshot)|timestamp)\.json$`)

type stagingFetcher struct {
	client *http.Client
	origin string
}

func stagingAssetURL(u *url.URL) bool {
	return u.Scheme == "https" && u.Host == "release-assets.githubusercontent.com" && u.User == nil && u.Fragment == "" && strings.HasPrefix(u.Path, "/github-production-release-asset/")
}
func newStagingFetcher(req request) (*stagingFetcher, error) {
	origin, pin := stagingOrigin, stagingRoot
	allowed := os.Getenv("USERNAME") == "WDAGUtilityAccount" || req.Transport == "pilot" && pilotEnabled == "1"
	if req.Transport == "stable" {
		origin, pin = stableOrigin, stableRoot
		allowed = (os.Getenv("USERNAME") == "WDAGUtilityAccount" || stableEnabled == "1") && len(stableRoot) == 64
	}
	if !allowed || req.Origin != origin || req.BootstrapHash != pin || req.TLSRoot != "" || req.TLSRootHash != "" {
		return nil, errors.New("feed needs its explicit enabled identity, origin and independent root pin")
	}
	transport := &http.Transport{TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12}, DisableCompression: true, TLSHandshakeTimeout: 15 * time.Second, ResponseHeaderTimeout: 20 * time.Second}
	client := &http.Client{Transport: transport, Timeout: 25 * time.Second, CheckRedirect: func(req *http.Request, via []*http.Request) error {
		if len(via) > 2 || !stagingAssetURL(req.URL) {
			return errors.New("staging redirect left the permitted release CDN")
		}
		return nil
	}}
	return &stagingFetcher{client: client, origin: origin}, nil
}
func (f *stagingFetcher) DownloadFile(location string, maximum int64, _ time.Duration) ([]byte, error) {
	if !strings.HasPrefix(location, f.origin) || !stagingMetadataName.MatchString(strings.TrimPrefix(location, f.origin)) {
		return nil, errors.New("metadata request left the staging release")
	}
	response, err := f.client.Get(location)
	if err != nil {
		var requestError *url.Error
		if errors.As(err, &requestError) {
			return nil, requestError.Err
		}
		return nil, errors.New("staging HTTPS request failed")
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return nil, &metadata.ErrDownloadHTTP{StatusCode: response.StatusCode, URL: location}
	}
	if response.Header.Get("Content-Encoding") != "" || response.ContentLength > maximum {
		return nil, errors.New("invalid staging metadata response")
	}
	bytes, err := io.ReadAll(io.LimitReader(response.Body, maximum+1))
	if int64(len(bytes)) > maximum {
		return nil, errors.New("staging metadata exceeded its bound")
	}
	return bytes, err
}

package main

import (
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/pem"
	"errors"
	"github.com/theupdateframework/go-tuf/v2/metadata"
	"io"
	"net"
	"net/http"
	"net/url"
	"path/filepath"
	"strings"
	"time"
)

type httpsFetcher struct {
	origin *url.URL
	client *http.Client
}

func newHTTPSFetcher(req request) (*httpsFetcher, error) {
	origin, err := url.Parse(req.Origin)
	if err != nil {
		return nil, err
	}
	ip := net.ParseIP(origin.Hostname())
	if origin.Scheme != "https" || ip == nil || !ip.IsLoopback() || origin.User != nil || origin.Path != "/" || origin.RawQuery != "" || origin.Fragment != "" {
		return nil, errors.New("only a loopback HTTPS test repository is permitted")
	}
	data, err := boundedRead(req.TLSRoot, 16384)
	if err != nil {
		return nil, err
	}
	block, rest := pem.Decode(data)
	if block == nil || block.Type != "CERTIFICATE" || len(strings.TrimSpace(string(rest))) != 0 {
		return nil, errors.New("invalid TLS root")
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return nil, err
	}
	hash := sha256.Sum256(cert.Raw)
	if strings.ToUpper(hex.EncodeToString(hash[:])) != req.TLSRootHash {
		return nil, errors.New("TLS root pin changed")
	}
	roots := x509.NewCertPool()
	roots.AddCert(cert)
	transport := &http.Transport{TLSClientConfig: &tls.Config{RootCAs: roots, MinVersion: tls.VersionTLS12}, DisableCompression: true, TLSHandshakeTimeout: 10 * time.Second, ResponseHeaderTimeout: 10 * time.Second}
	client := &http.Client{Transport: transport, Timeout: 20 * time.Second, CheckRedirect: func(_ *http.Request, _ []*http.Request) error { return errors.New("metadata redirects are disabled") }}
	return &httpsFetcher{origin, client}, nil
}
func (f *httpsFetcher) DownloadFile(location string, max int64, _ time.Duration) ([]byte, error) {
	u, err := url.Parse(location)
	if err != nil {
		return nil, err
	}
	if u.Scheme != f.origin.Scheme || u.Host != f.origin.Host || u.User != nil || u.RawQuery != "" || u.Fragment != "" || !strings.HasPrefix(u.Path, "/metadata/") || filepath.Base(u.Path) != strings.TrimPrefix(u.Path, "/metadata/") || strings.ContainsAny(u.Path, "\\:") {
		return nil, errors.New("metadata request left the bound HTTPS repository")
	}
	response, err := f.client.Get(location)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	if response.StatusCode != 200 {
		return nil, &metadata.ErrDownloadHTTP{StatusCode: response.StatusCode, URL: location}
	}
	if response.Header.Get("Content-Encoding") != "" || response.ContentLength > max {
		return nil, errors.New("invalid metadata response")
	}
	data, err := io.ReadAll(io.LimitReader(response.Body, max+1))
	if len(data) > int(max) {
		return nil, errors.New("metadata exceeded its bound")
	}
	return data, err
}

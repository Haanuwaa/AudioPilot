package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/pem"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestHTTPSFetcherBoundaries(t *testing.T) {
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/metadata/redirect.json" {
			http.Redirect(w, r, "https://example.com/", 302)
			return
		}
		w.Write([]byte("metadata"))
	}))
	defer server.Close()
	cert := server.Certificate()
	hash := sha256.Sum256(cert.Raw)
	root := filepath.Join(t.TempDir(), "root.pem")
	if err := os.WriteFile(root, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: cert.Raw}), 0600); err != nil {
		t.Fatal(err)
	}
	req := request{Origin: server.URL + "/", TLSRoot: root, TLSRootHash: strings.ToUpper(hex.EncodeToString(hash[:]))}
	f, err := newHTTPSFetcher(req)
	if err != nil {
		t.Fatal(err)
	}
	defer f.client.CloseIdleConnections()
	data, err := f.DownloadFile(server.URL+"/metadata/timestamp.json", 20, 0)
	if err != nil || string(data) != "metadata" {
		t.Fatalf("validated HTTPS read: %s %v", data, err)
	}
	for _, url := range []string{server.URL + "/metadata/redirect.json", server.URL + "/targets/file.zip", "http://127.0.0.1/metadata/timestamp.json", "https://example.com/metadata/timestamp.json"} {
		if _, err = f.DownloadFile(url, 20, 0); err == nil {
			t.Fatalf("accepted unsafe URL %s", url)
		}
	}
	if _, err = f.DownloadFile(server.URL+"/metadata/timestamp.json", 3, 0); err == nil {
		t.Fatal("oversized metadata accepted")
	}
	req.TLSRootHash = strings.Repeat("0", 64)
	if _, err = newHTTPSFetcher(req); err == nil {
		t.Fatal("incorrect TLS root pin accepted")
	}
	req.Origin = "https://example.com/"
	if _, err = newHTTPSFetcher(req); err == nil {
		t.Fatal("public repository accepted")
	}
}

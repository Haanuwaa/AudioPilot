package main

import (
	"bytes"
	"errors"
	"fmt"
	"net"
	"strings"
	"testing"

	"github.com/theupdateframework/go-tuf/v2/metadata"
)

func TestBridgeFailuresPreserveSafeReasonsWithoutPrivateDetails(t *testing.T) {
	for _, test := range []struct {
		err    error
		reason string
	}{
		{fmt.Errorf("private path: %w", &metadata.ErrExpiredMetadata{Msg: "private"}), "feed-expired"},
		{&bridgeReasonError{"feed-trust-state", errors.New("private cache path")}, "feed-trust-state"},
		{&metadata.ErrDownloadHTTP{StatusCode: 429, URL: "https://private.invalid/token"}, "feed-rate-limited"},
		{&metadata.ErrDownloadHTTP{StatusCode: 403, URL: "https://private.invalid/token"}, "feed-access-denied"},
		{&net.DNSError{Err: "private", Name: "private.invalid"}, "feed-network"},
		{errors.New("private signature details"), "feed-verification-failed"},
	} {
		var output bytes.Buffer
		if err := writeFailure(&output, test.err); err != nil {
			t.Fatal(err)
		}
		if failureReason(test.err) != test.reason || !strings.Contains(output.String(), test.reason) || strings.Contains(output.String(), "private") {
			t.Fatalf("incorrect or private error response: %s", output.String())
		}
	}
}

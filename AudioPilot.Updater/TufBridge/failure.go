package main

import (
	"encoding/json"
	"errors"
	"io"
	"net"

	"github.com/theupdateframework/go-tuf/v2/metadata"
)

type bridgeFailure struct {
	Schema int    `json:"schema"`
	Reason string `json:"reason"`
}

type bridgeReasonError struct {
	reason string
	cause  error
}

func (e *bridgeReasonError) Error() string { return e.cause.Error() }
func (e *bridgeReasonError) Unwrap() error { return e.cause }

func failureReason(err error) string {
	var explicit *bridgeReasonError
	if errors.As(err, &explicit) {
		return explicit.reason
	}
	var expired *metadata.ErrExpiredMetadata
	if errors.As(err, &expired) {
		return "feed-expired"
	}
	var http *metadata.ErrDownloadHTTP
	if errors.As(err, &http) {
		switch {
		case http.StatusCode == 429:
			return "feed-rate-limited"
		case http.StatusCode == 401 || http.StatusCode == 403:
			return "feed-access-denied"
		case http.StatusCode == 404 || http.StatusCode >= 500:
			return "feed-unavailable"
		}
	}
	var network net.Error
	if errors.As(err, &network) {
		return "feed-network"
	}
	return "feed-verification-failed"
}

func writeFailure(output io.Writer, err error) error {
	// Only a reason code crosses the process boundary; paths and server-provided text stay private.
	return json.NewEncoder(output).Encode(bridgeFailure{1, failureReason(err)})
}

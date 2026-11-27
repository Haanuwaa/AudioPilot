package staging

import (
	"errors"

	"github.com/theupdateframework/go-tuf/v2/metadata"
)

const channelField = "x-audiopilot-channel"

func feedChannel(req Request) string {
	if req.Channel == "" {
		return "staging"
	}
	return req.Channel
}

func rootChannel(root *metadata.Metadata[metadata.RootType]) string {
	if value, exists := root.Signed.UnrecognizedFields[channelField]; exists {
		channel, _ := value.(string)
		return channel
	}
	return "staging"
}

func requireChannel(root *metadata.Metadata[metadata.RootType], req Request) error {
	if rootChannel(root) != feedChannel(req) {
		return errors.New("authority root belongs to another update channel")
	}
	return nil
}

func validWindowsTrust(channel, trust string) bool {
	if channel == "stable" {
		return trust == "self-signed"
	}
	return trust == "self-signed-staging" || trust == "publicly-trusted"
}

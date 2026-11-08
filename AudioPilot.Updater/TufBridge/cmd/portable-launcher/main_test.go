package main

import "testing"

func TestReadStateRejectsAmbiguityAndEscapingGenerations(t *testing.T) {
	valid := `{"Schema":1,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":false}`
	if _, err := readState([]byte(valid)); err != nil {
		t.Fatal(err)
	}
	for _, invalid := range []string{
		`{"Schema":1,"Schema":2,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":false}`,
		`{"Schema":1,"Current":"../elsewhere","Previous":null,"PendingHealth":false}`,
		`{"Schema":1,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":true}`,
		`{"Schema":1,"Current":"1.0.0_0123456789abcdef0123456789abcdef","Previous":null,"PendingHealth":null}`,
		valid + `{}`,
	} {
		if _, err := readState([]byte(invalid)); err == nil {
			t.Fatal("unsafe launcher state was accepted")
		}
	}
}

package main

import (
	"context"
	"errors"
	"io"
	"strings"
	"testing"
	"time"
)

func TestParentPipeLossCancelsGateway(t *testing.T) {
	for _, readError := range []error{nil, errors.New("parent pipe lost")} {
		name := "EOF"
		if readError != nil {
			name = "read error"
		}
		t.Run(name, func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			input, parent := io.Pipe()
			defer input.Close()
			defer parent.Close()
			watchParentPipe("true", input, cancel)
			written := make(chan error, 1)
			go func() { _, err := parent.Write([]byte("still alive")); written <- err }()
			select {
			case err := <-written:
				if err != nil {
					t.Fatal(err)
				}
			case <-time.After(time.Second):
				t.Fatal("parent pipe was not read")
			}
			if ctx.Err() != nil {
				t.Fatal("reading pipe data cancelled the gateway")
			}
			if err := parent.CloseWithError(readError); err != nil {
				t.Fatal(err)
			}
			select {
			case <-ctx.Done():
			case <-time.After(time.Second):
				t.Fatal("parent pipe loss did not cancel the gateway")
			}
		})
	}
}

func TestStandaloneGatewayIgnoresStdinEOF(t *testing.T) {
	for _, marker := range []string{"", "false"} {
		t.Run("marker="+marker, func(t *testing.T) {
			ctx, cancel := context.WithCancel(context.Background())
			defer cancel()
			watchParentPipe(marker, strings.NewReader(""), cancel)
			select {
			case <-ctx.Done():
				t.Fatal("standalone gateway treated stdin EOF as parent loss")
			case <-time.After(20 * time.Millisecond):
			}
		})
	}
}

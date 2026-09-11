package main

import (
	"bytes"
	"encoding/binary"
	"testing"
)

func TestFrameRejectsUnboundedPayload(t *testing.T) {
	var header [6]byte
	header[0] = frameData
	binary.LittleEndian.PutUint32(header[2:], maxPayload+1)
	if _, err := readFrame(bytes.NewReader(header[:])); err == nil {
		t.Fatal("oversized frame accepted")
	}
	if err := writeFrame(&bytes.Buffer{}, frame{payload: make([]byte, maxPayload+1)}); err == nil {
		t.Fatal("oversized write accepted")
	}
}

func TestSequenceWrapAndDuplicates(t *testing.T) {
	tracker := sequenceTracker{}
	for _, sequence := range []uint32{0xfffffffe, 0xffffffff, 0, 1} {
		if !tracker.accept(sequence) {
			t.Fatalf("valid sequence %d rejected", sequence)
		}
	}
	for _, sequence := range []uint32{1, 0, 0xffffffff, 0x80000001} {
		if tracker.accept(sequence) {
			t.Fatalf("old sequence %d accepted", sequence)
		}
	}
}

func TestBackpressureRetainsNewerSnapshotsAndReliableOrdering(t *testing.T) {
	q := newFrameQueue()
	q.maxFrames = 3
	q.maxBytes = 3
	for _, f := range []frame{{delivery: 2, payload: []byte{1}}, {delivery: 1, payload: []byte{2}}, {delivery: 4, payload: []byte{3}}, {delivery: 1, payload: []byte{4}}} {
		if !q.push(f) {
			t.Fatal("unexpected overflow")
		}
	}
	for _, want := range []byte{1, 3, 4} {
		f, ok := q.pop()
		if !ok || f.payload[0] != want {
			t.Fatalf("got %+v, expected %d", f, want)
		}
	}
	for range 3 {
		if !q.push(frame{delivery: 2, payload: []byte{1}}) {
			t.Fatal("unexpected overflow")
		}
	}
	if q.push(frame{delivery: 0, payload: []byte{2}}) {
		t.Fatal("reliable overflow must disconnect")
	}
	if !q.push(frame{delivery: 4, payload: []byte{3}}) {
		t.Fatal("unreliable overflow should drop")
	}
}

func TestConfigRequiresPrivateAuthenticatedBridge(t *testing.T) {
	t.Setenv("PURR_WEBRTC_BRIDGE_TOKEN", "")
	if _, err := loadConfig(); err == nil {
		t.Fatal("empty token accepted")
	}
	for _, address := range []string{"0.0.0.0:8090", "localhost:8090", "192.168.0.1:8090"} {
		if requireLoopback(address) == nil {
			t.Fatalf("public/ambiguous address accepted: %s", address)
		}
	}
	for _, address := range []string{"127.0.0.1:8090", "[::1]:8090"} {
		if err := requireLoopback(address); err != nil {
			t.Fatal(err)
		}
	}
}

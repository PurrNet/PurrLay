package main

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"testing"
	"time"

	"github.com/pion/webrtc/v4"
)

type received struct {
	delivery byte
	data     []byte
}

type harness struct {
	gateway  *gateway
	server   *httptest.Server
	client   *webrtc.PeerConnection
	channels map[byte]*webrtc.DataChannel
	bridge   net.Conn
	received chan received
	opened   chan byte
}

func newHarness(t *testing.T) *harness {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = listener.Close() })
	accepted := make(chan net.Conn, 1)
	bridgeErrors := make(chan error, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			bridgeErrors <- err
			return
		}
		_ = conn.SetDeadline(time.Now().Add(10 * time.Second))
		hello, err := readFrame(conn)
		if err != nil {
			_ = conn.Close()
			bridgeErrors <- err
			return
		}
		if hello.kind != frameHello || hello.delivery != 0 || string(hello.payload) != "integration-token" {
			_ = conn.Close()
			bridgeErrors <- io.ErrUnexpectedEOF
			return
		}
		if err = writeFrame(conn, frame{kind: frameHello}); err != nil {
			_ = conn.Close()
			bridgeErrors <- err
			return
		}
		_ = conn.SetDeadline(time.Time{})
		accepted <- conn
	}()
	g, err := newGateway(config{bridgeAddress: listener.Addr().String(), bridgeToken: "integration-token", bindAddress: "127.0.0.1", udpPort: 0, maxSessions: 8})
	if err != nil {
		t.Fatal(err)
	}
	h := &harness{gateway: g, server: httptest.NewServer(g.handler()), channels: make(map[byte]*webrtc.DataChannel), received: make(chan received, 32), opened: make(chan byte, 4)}
	t.Cleanup(func() {
		h.server.Close()
		g.close()
		if h.client != nil {
			_ = h.client.Close()
		}
		if h.bridge != nil {
			_ = h.bridge.Close()
		}
	})
	settings := webrtc.SettingEngine{}
	settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	settings.SetSCTPMaxMessageSize(maxPayload)
	settings.SetIncludeLoopbackCandidate(true)
	settings.SetIPFilter(func(ip net.IP) bool { return ip.IsLoopback() })
	h.client, err = webrtc.NewAPI(webrtc.WithSettingEngine(settings)).NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		t.Fatal(err)
	}
	for _, delivery := range []byte{0, 1, 2, 4} {
		ordered := delivery == 2
		options := &webrtc.DataChannelInit{Ordered: &ordered}
		zero := uint16(0)
		if isUnreliable(delivery) {
			options.MaxRetransmits = &zero
		}
		dc, err := h.client.CreateDataChannel("purr-"+strconv.Itoa(int(delivery)), options)
		if err != nil {
			t.Fatal(err)
		}
		h.channels[delivery] = dc
		dc.OnOpen(func() { h.opened <- delivery })
		dc.OnMessage(func(message webrtc.DataChannelMessage) {
			h.received <- received{delivery, append([]byte(nil), message.Data...)}
		})
	}
	offer, err := h.client.CreateOffer(nil)
	if err != nil {
		t.Fatal(err)
	}
	gathered := webrtc.GatheringCompletePromise(h.client)
	if err = h.client.SetLocalDescription(offer); err != nil {
		t.Fatal(err)
	}
	select {
	case <-gathered:
	case <-time.After(10 * time.Second):
		t.Fatal("client ICE gathering timed out")
	}
	encoded, err := json.Marshal(h.client.LocalDescription())
	if err != nil {
		t.Fatal(err)
	}
	response, err := http.Post(h.server.URL+"/offer", "application/json", bytes.NewReader(encoded))
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(response.Body)
		t.Fatalf("offer: %s %s", response.Status, body)
	}
	var answer webrtc.SessionDescription
	if err = json.NewDecoder(response.Body).Decode(&answer); err != nil {
		t.Fatal(err)
	}
	if answer.Type != webrtc.SDPTypeAnswer {
		t.Fatalf("answer type %s", answer.Type)
	}
	select {
	case h.bridge = <-accepted:
	case err := <-bridgeErrors:
		t.Fatal(err)
	case <-time.After(10 * time.Second):
		t.Fatal("no bridge accepted")
	}
	if err = h.client.SetRemoteDescription(answer); err != nil {
		t.Fatal(err)
	}
	for range 4 {
		select {
		case <-h.opened:
		case <-time.After(10 * time.Second):
			t.Fatal("data channels did not open")
		}
	}
	return h
}

func readBridgeTest(t *testing.T, h *harness) frame {
	t.Helper()
	_ = h.bridge.SetReadDeadline(time.Now().Add(5 * time.Second))
	f, err := readFrame(h.bridge)
	if err != nil {
		t.Fatal(err)
	}
	return f
}

func receiveTest(t *testing.T, h *harness) received {
	t.Helper()
	select {
	case result := <-h.received:
		return result
	case <-time.After(5 * time.Second):
		t.Fatal("no browser message")
		return received{}
	}
}

func sequenced(sequence uint32, payload []byte) []byte {
	result := make([]byte, len(payload)+4)
	binary.LittleEndian.PutUint32(result, sequence)
	copy(result[4:], payload)
	return result
}

func TestPionBridgeAllDeliveryModes(t *testing.T) {
	h := newHarness(t)
	response, err := http.Get(h.server.URL + "/health")
	if err != nil {
		t.Fatal(err)
	}
	_ = response.Body.Close()
	if response.StatusCode != http.StatusOK {
		t.Fatal(response.Status)
	}
	for _, delivery := range []byte{0, 1, 2, 4} {
		payload := []byte{delivery, 10, 20, 30}
		if delivery == 1 {
			payload = sequenced(0, payload)
		}
		if err = h.channels[delivery].Send(payload); err != nil {
			t.Fatal(err)
		}
		f := readBridgeTest(t, h)
		if f.kind != frameData || f.delivery != delivery || !bytes.Equal(f.payload, []byte{delivery, 10, 20, 30}) {
			t.Fatalf("unexpected bridge frame: %+v", f)
		}
		if err = writeFrame(h.bridge, f); err != nil {
			t.Fatal(err)
		}
		got := receiveTest(t, h)
		expected := f.payload
		if delivery == 1 {
			expected = sequenced(0, expected)
		}
		if got.delivery != delivery || !bytes.Equal(got.data, expected) {
			t.Fatalf("unexpected browser message: %+v", got)
		}
	}
	// Relay keepalive mode 3 is intentionally sent on reliable ordered mode 2.
	if err = writeFrame(h.bridge, frame{kind: frameData, delivery: 3, payload: []byte("keepalive")}); err != nil {
		t.Fatal(err)
	}
	got := receiveTest(t, h)
	if got.delivery != 2 || string(got.data) != "keepalive" {
		t.Fatalf("keepalive mapping: %+v", got)
	}
	if err = h.channels[1].Send(sequenced(10, []byte("newer"))); err != nil {
		t.Fatal(err)
	}
	if f := readBridgeTest(t, h); string(f.payload) != "newer" {
		t.Fatalf("unexpected snapshot %+v", f)
	}
	for _, sequence := range []uint32{9, 10, 11} {
		if err = h.channels[1].Send(sequenced(sequence, []byte{byte(sequence)})); err != nil {
			t.Fatal(err)
		}
	}
	if f := readBridgeTest(t, h); !bytes.Equal(f.payload, []byte{11}) {
		t.Fatalf("stale sequence forwarded: %+v", f)
	}
	_ = h.bridge.Close()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		h.gateway.mu.Lock()
		count := h.gateway.reservations
		h.gateway.mu.Unlock()
		if count == 0 {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatal("bridge EOF did not clean up session")
}

func TestPionPeerCloseClosesBridge(t *testing.T) {
	h := newHarness(t)
	if err := h.client.Close(); err != nil {
		t.Fatal(err)
	}
	_ = h.bridge.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, err := readFrame(h.bridge); err == nil {
		t.Fatal("expected bridge EOF after peer close")
	} else if timeout, ok := err.(net.Error); ok && timeout.Timeout() {
		t.Fatal("bridge stayed open after peer close")
	}
}

func TestPionRejectsUnexpectedChannel(t *testing.T) {
	h := newHarness(t)
	ordered := false
	if _, err := h.client.CreateDataChannel("purr-4", &webrtc.DataChannelInit{Ordered: &ordered}); err != nil {
		t.Fatal(err)
	}
	_ = h.bridge.SetReadDeadline(time.Now().Add(5 * time.Second))
	if _, err := readFrame(h.bridge); err == nil {
		t.Fatal("invalid channel did not close bridge")
	} else if timeout, ok := err.(net.Error); ok && timeout.Timeout() {
		t.Fatal("invalid channel was accepted")
	}
}

func TestPionMaximumSequencedPayload(t *testing.T) {
	h := newHarness(t)
	payload := bytes.Repeat([]byte{0x41}, maxPayload-4)
	if err := h.channels[1].Send(sequenced(0, payload)); err != nil {
		t.Fatal(err)
	}
	f := readBridgeTest(t, h)
	if f.delivery != 1 || !bytes.Equal(f.payload, payload) {
		t.Fatal("maximum sequenced payload corrupted")
	}
	if err := writeFrame(h.bridge, f); err != nil {
		t.Fatal(err)
	}
	got := receiveTest(t, h)
	if got.delivery != 1 || !bytes.Equal(got.data, sequenced(0, payload)) {
		t.Fatal("maximum outbound sequenced payload corrupted")
	}
}

func TestOfferFailureReleasesCapacity(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	address := listener.Addr().String()
	_ = listener.Close()
	g, err := newGateway(config{bridgeAddress: address, bridgeToken: "token", bindAddress: "127.0.0.1", udpPort: 0, maxSessions: 1})
	if err != nil {
		t.Fatal(err)
	}
	defer g.close()
	for _, body := range []string{`{"type":"answer","sdp":"bad"}`, `{"type":"offer","sdp":"bad"}`} {
		request := httptest.NewRequest(http.MethodPost, "/offer", bytes.NewBufferString(body))
		response := httptest.NewRecorder()
		g.handler().ServeHTTP(response, request)
		if response.Code == http.StatusOK {
			t.Fatal("invalid/unavailable offer succeeded")
		}
		g.mu.Lock()
		count := g.reservations
		g.mu.Unlock()
		if count != 0 {
			t.Fatal("failed offer retained capacity")
		}
	}
	if !g.reserve() {
		t.Fatal("could not reserve slot")
	}
	response := httptest.NewRecorder()
	g.handler().ServeHTTP(response, httptest.NewRequest(http.MethodPost, "/offer", bytes.NewBufferString(`{}`)))
	if response.Code != http.StatusServiceUnavailable {
		t.Fatal("session cap not enforced")
	}
	g.release(nil)
}

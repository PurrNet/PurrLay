package main

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log"
	"net"
	"net/http"
	"strconv"
	"sync"
	"time"

	"github.com/pion/ice/v4"
	"github.com/pion/webrtc/v4"
)

type gateway struct {
	config       config
	api          *webrtc.API
	mux          ice.UDPMux
	mu           sync.Mutex
	sessions     map[*session]struct{}
	reservations int
	closed       bool
}

func newGateway(c config) (*gateway, error) {
	address, err := net.ResolveUDPAddr("udp4", net.JoinHostPort(c.bindAddress, strconv.Itoa(c.udpPort)))
	if err != nil {
		return nil, err
	}
	conn, err := net.ListenUDP("udp4", address)
	if err != nil {
		return nil, err
	}
	mux := ice.NewUDPMuxDefault(ice.UDPMuxParams{UDPConn: conn})
	settings := webrtc.SettingEngine{}
	settings.SetICEUDPMux(mux)
	settings.SetNetworkTypes([]webrtc.NetworkType{webrtc.NetworkTypeUDP4})
	if ip := net.ParseIP(c.bindAddress); ip != nil && ip.IsLoopback() {
		settings.SetIncludeLoopbackCandidate(true)
	}
	settings.SetSCTPMaxReceiveBufferSize(1024 * 1024)
	settings.SetSCTPMaxMessageSize(maxPayload)
	settings.SetICETimeouts(10*time.Second, 20*time.Second, 2*time.Second)
	if len(c.publicIPs) > 0 {
		settings.SetNAT1To1IPs(c.publicIPs, webrtc.ICECandidateTypeHost)
	}
	return &gateway{config: c, api: webrtc.NewAPI(webrtc.WithSettingEngine(settings)), mux: mux, sessions: make(map[*session]struct{})}, nil
}

func (g *gateway) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /health", func(w http.ResponseWriter, r *http.Request) {
		g.mu.Lock()
		closed := g.closed
		g.mu.Unlock()
		if closed {
			http.Error(w, "shutting down", http.StatusServiceUnavailable)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, "{\"status\":\"ok\"}")
	})
	mux.HandleFunc("POST /offer", g.offer)
	return mux
}

func (g *gateway) reserve() bool {
	g.mu.Lock()
	defer g.mu.Unlock()
	if g.closed || g.reservations >= g.config.maxSessions {
		return false
	}
	g.reservations++
	return true
}

func (g *gateway) release(s *session) {
	g.mu.Lock()
	defer g.mu.Unlock()
	if s != nil {
		delete(g.sessions, s)
	}
	g.reservations--
}

func (g *gateway) offer(w http.ResponseWriter, r *http.Request) {
	if !g.reserve() {
		http.Error(w, "gateway at capacity", http.StatusServiceUnavailable)
		return
	}
	var s *session
	defer func() {
		if s == nil {
			g.release(nil)
		}
	}()
	r.Body = http.MaxBytesReader(w, r.Body, 128*1024)
	var offer webrtc.SessionDescription
	decoder := json.NewDecoder(r.Body)
	if err := decoder.Decode(&offer); err != nil || offer.Type != webrtc.SDPTypeOffer || offer.SDP == "" {
		http.Error(w, "invalid WebRTC offer", http.StatusBadRequest)
		return
	}
	var extra any
	if err := decoder.Decode(&extra); err != io.EOF {
		http.Error(w, "invalid WebRTC offer", http.StatusBadRequest)
		return
	}
	ctx, cancel := context.WithTimeout(r.Context(), setupTimeout)
	defer cancel()
	pc, err := g.api.NewPeerConnection(webrtc.Configuration{})
	if err != nil {
		http.Error(w, "peer initialization failed", http.StatusServiceUnavailable)
		return
	}
	bridge, err := (&net.Dialer{}).DialContext(ctx, "tcp", g.config.bridgeAddress)
	if err != nil {
		_ = pc.Close()
		http.Error(w, "relay unavailable", http.StatusServiceUnavailable)
		return
	}
	deadline, _ := ctx.Deadline()
	_ = bridge.SetDeadline(deadline)
	err = writeFrame(bridge, frame{kind: frameHello, payload: []byte(g.config.bridgeToken)})
	var reply frame
	if err == nil {
		reply, err = readFrame(bridge)
	}
	if err != nil || reply.kind != frameHello || reply.delivery != 0 || len(reply.payload) != 0 {
		_ = bridge.Close()
		_ = pc.Close()
		http.Error(w, "relay authentication failed", http.StatusServiceUnavailable)
		return
	}
	_ = bridge.SetDeadline(time.Time{})
	s = newSession(pc, bridge, g.release)
	g.mu.Lock()
	if g.closed {
		g.mu.Unlock()
		s.close()
		http.Error(w, "shutting down", http.StatusServiceUnavailable)
		return
	}
	g.sessions[s] = struct{}{}
	g.mu.Unlock()
	s.start()
	ok := false
	defer func() {
		if !ok {
			s.close()
		}
	}()
	if err = pc.SetRemoteDescription(offer); err != nil {
		http.Error(w, "invalid WebRTC offer", http.StatusBadRequest)
		return
	}
	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		http.Error(w, "cannot create answer", http.StatusBadRequest)
		return
	}
	gathered := webrtc.GatheringCompletePromise(pc)
	if err = pc.SetLocalDescription(answer); err != nil {
		http.Error(w, "cannot initialize answer", http.StatusServiceUnavailable)
		return
	}
	select {
	case <-gathered:
	case <-ctx.Done():
		http.Error(w, "WebRTC setup timed out", http.StatusGatewayTimeout)
		return
	case <-s.done:
		http.Error(w, "relay disconnected", http.StatusServiceUnavailable)
		return
	}
	answerJSON, err := json.Marshal(pc.LocalDescription())
	if err != nil || len(answerJSON) > 128*1024 {
		http.Error(w, "invalid WebRTC answer", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Cache-Control", "no-store")
	if _, err = w.Write(answerJSON); err != nil {
		return
	}
	ok = true
}

func (g *gateway) close() {
	g.mu.Lock()
	if g.closed {
		g.mu.Unlock()
		return
	}
	g.closed = true
	peers := make([]*session, 0, len(g.sessions))
	for s := range g.sessions {
		peers = append(peers, s)
	}
	g.mu.Unlock()
	for _, s := range peers {
		s.close()
	}
	for _, s := range peers {
		<-s.cleaned
	}
	_ = g.mux.Close()
}

func serve(ctx context.Context, c config) error {
	g, err := newGateway(c)
	if err != nil {
		return err
	}
	defer g.close()
	listener, err := net.Listen("tcp", c.httpAddress)
	if err != nil {
		return err
	}
	server := &http.Server{Handler: g.handler(), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 25 * time.Second, WriteTimeout: 25 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192}
	done := make(chan error, 1)
	go func() { done <- server.Serve(listener) }()
	log.Printf("WebRTC gateway ready: signaling=%s UDP=%s:%d", c.httpAddress, c.bindAddress, c.udpPort)
	select {
	case err = <-done:
		if !errors.Is(err, http.ErrServerClosed) {
			return err
		}
	case <-ctx.Done():
		g.close()
		shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := server.Shutdown(shutdown); err != nil {
			_ = server.Close()
		}
	}
	return nil
}

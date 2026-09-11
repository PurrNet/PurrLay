package main

import (
	"encoding/binary"
	"net"
	"sync"
	"time"

	"github.com/pion/webrtc/v4"
)

type channelState struct {
	dc    *webrtc.DataChannel
	ready chan struct{}
}

type session struct {
	pc               *webrtc.PeerConnection
	bridge           net.Conn
	done             chan struct{}
	cleaned          chan struct{}
	closeOnce        sync.Once
	onClose          func(*session)
	toBridge         *frameQueue
	toBrowser        *frameQueue
	mu               sync.Mutex
	channels         map[byte]*channelState
	opened           int
	allOpen          chan struct{}
	incomingSequence sequenceTracker
	outgoingSequence uint32
}

func newSession(pc *webrtc.PeerConnection, bridge net.Conn, onClose func(*session)) *session {
	s := &session{pc: pc, bridge: bridge, done: make(chan struct{}), cleaned: make(chan struct{}), onClose: onClose, toBridge: newFrameQueue(), toBrowser: newFrameQueue(), channels: make(map[byte]*channelState), allOpen: make(chan struct{})}
	for _, delivery := range []byte{0, 1, 2, 4} {
		s.channels[delivery] = &channelState{ready: make(chan struct{})}
	}
	return s
}

func (s *session) start() {
	s.pc.OnDataChannel(s.addChannel)
	s.pc.OnConnectionStateChange(func(state webrtc.PeerConnectionState) {
		if state == webrtc.PeerConnectionStateFailed || state == webrtc.PeerConnectionStateClosed {
			s.close()
		}
	})
	go s.readBridge()
	go s.writeBridge()
	go s.writeBrowser()
	go func() {
		timer := time.NewTimer(setupTimeout)
		defer timer.Stop()
		select {
		case <-s.allOpen:
		case <-s.done:
		case <-timer.C:
			s.close()
		}
	}()
}

func (s *session) close() {
	s.closeOnce.Do(func() {
		close(s.done)
		_ = s.bridge.Close()
		// Pion can invoke a callback from a goroutine that Close must join.
		// Schedule its cleanup outside that callback to avoid self-deadlock.
		go func() {
			_ = s.pc.Close()
			if s.onClose != nil {
				s.onClose(s)
			}
			close(s.cleaned)
		}()
	})
}

func deliveryForChannel(dc *webrtc.DataChannel) (byte, bool) {
	var delivery byte
	switch dc.Label() {
	case "purr-0":
		delivery = 0
	case "purr-1":
		delivery = 1
	case "purr-2":
		delivery = 2
	case "purr-4":
		delivery = 4
	default:
		return 0, false
	}
	if dc.Protocol() != "" || dc.Negotiated() || dc.Ordered() != (delivery == 2) || dc.MaxPacketLifeTime() != nil {
		return 0, false
	}
	if isUnreliable(delivery) {
		return delivery, dc.MaxRetransmits() != nil && *dc.MaxRetransmits() == 0
	}
	return delivery, dc.MaxRetransmits() == nil
}

func (s *session) addChannel(dc *webrtc.DataChannel) {
	delivery, valid := deliveryForChannel(dc)
	if !valid {
		s.close()
		return
	}
	s.mu.Lock()
	state := s.channels[delivery]
	if state.dc != nil {
		s.mu.Unlock()
		s.close()
		return
	}
	state.dc = dc
	s.mu.Unlock()
	dc.OnOpen(func() {
		s.mu.Lock()
		select {
		case <-state.ready:
		default:
			close(state.ready)
			s.opened++
			if s.opened == 4 {
				close(s.allOpen)
			}
		}
		s.mu.Unlock()
	})
	dc.OnClose(s.close)
	dc.OnError(func(error) { s.close() })
	dc.OnMessage(func(message webrtc.DataChannelMessage) { s.receiveBrowser(delivery, message) })
}

func (s *session) receiveBrowser(delivery byte, message webrtc.DataChannelMessage) {
	if message.IsString || len(message.Data) > maxPayload {
		s.close()
		return
	}
	data := message.Data
	if delivery == 1 {
		if len(data) < 4 {
			s.close()
			return
		}
		sequence := binary.LittleEndian.Uint32(data[:4])
		s.mu.Lock()
		accepted := s.incomingSequence.accept(sequence)
		s.mu.Unlock()
		if !accepted {
			return
		}
		data = data[4:]
	}
	select {
	case <-s.done:
		return
	default:
	}
	// Never retain memory owned by the transport callback.
	f := frame{kind: frameData, delivery: delivery, payload: append([]byte(nil), data...)}
	if !s.toBridge.push(f) {
		s.close()
	}
}

func (s *session) readBridge() {
	defer s.close()
	for {
		f, err := readFrame(s.bridge)
		if err != nil {
			return
		}
		if f.kind != frameData {
			return
		}
		delivery, valid := normalizeDelivery(f.delivery)
		if !valid {
			return
		}
		f.delivery = delivery
		if delivery == 1 && len(f.payload) > maxPayload-4 {
			return
		}
		if !s.toBrowser.push(f) {
			return
		}
	}
}

func (s *session) writeBridge() {
	defer s.close()
	for {
		select {
		case <-s.done:
			return
		default:
		}
		f, ok := s.toBridge.pop()
		if !ok {
			select {
			case <-s.done:
				return
			case <-s.toBridge.wake:
				continue
			}
		}
		_ = s.bridge.SetWriteDeadline(time.Now().Add(bridgeWriteTimeout))
		if err := writeFrame(s.bridge, f); err != nil {
			return
		}
	}
}

func (s *session) writeBrowser() {
	defer s.close()
	for {
		select {
		case <-s.done:
			return
		default:
		}
		f, ok := s.toBrowser.pop()
		if !ok {
			select {
			case <-s.done:
				return
			case <-s.toBrowser.wake:
				continue
			}
		}
		s.mu.Lock()
		state := s.channels[f.delivery]
		s.mu.Unlock()
		select {
		case <-s.done:
			return
		case <-state.ready:
		}
		s.mu.Lock()
		dc := state.dc
		s.mu.Unlock()
		// maxRetransmits=0 does not stop the initial transmission queue from
		// growing. Apply backpressure before handing a stale update to SCTP.
		if isUnreliable(f.delivery) && dc.BufferedAmount() > 64*1024 {
			continue
		}
		if dc.BufferedAmount()+uint64(len(f.payload)) > 1024*1024 {
			return
		}
		payload := f.payload
		if f.delivery == 1 {
			payload = make([]byte, len(f.payload)+4)
			binary.LittleEndian.PutUint32(payload, s.outgoingSequence)
			s.outgoingSequence++
			copy(payload[4:], f.payload)
		}
		if err := dc.Send(payload); err != nil {
			return
		}
	}
}

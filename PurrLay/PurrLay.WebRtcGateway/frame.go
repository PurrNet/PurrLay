package main

import (
	"encoding/binary"
	"errors"
	"io"
)

const (
	frameHello byte = 0
	frameData  byte = 1
)

type frame struct {
	kind     byte
	delivery byte
	payload  []byte
}

func readFrame(r io.Reader) (frame, error) {
	var header [6]byte
	if _, err := io.ReadFull(r, header[:]); err != nil {
		return frame{}, err
	}
	size := binary.LittleEndian.Uint32(header[2:])
	if size > maxPayload {
		return frame{}, errors.New("bridge payload exceeds 65535 bytes")
	}
	f := frame{kind: header[0], delivery: header[1], payload: make([]byte, int(size))}
	_, err := io.ReadFull(r, f.payload)
	return f, err
}

func writeFrame(w io.Writer, f frame) error {
	if len(f.payload) > maxPayload {
		return errors.New("bridge payload exceeds 65535 bytes")
	}
	var header [6]byte
	header[0], header[1] = f.kind, f.delivery
	binary.LittleEndian.PutUint32(header[2:], uint32(len(f.payload)))
	if err := writeAll(w, header[:]); err != nil {
		return err
	}
	return writeAll(w, f.payload)
}

func writeAll(w io.Writer, data []byte) error {
	for len(data) > 0 {
		n, err := w.Write(data)
		if err != nil {
			return err
		}
		if n == 0 {
			return io.ErrShortWrite
		}
		data = data[n:]
	}
	return nil
}

func isUnreliable(delivery byte) bool { return delivery == 1 || delivery == 4 }

func normalizeDelivery(delivery byte) (byte, bool) {
	switch delivery {
	case 0, 1, 2, 4:
		return delivery, true
	case 3:
		return 2, true
	default:
		return 0, false
	}
}

// Sequence comparison handles uint32 wraparound as long as peers never have
// more than half the sequence space in flight. Zero is a valid first sequence.
type sequenceTracker struct {
	seen bool
	last uint32
}

func (s *sequenceTracker) accept(next uint32) bool {
	if s.seen && int32(next-s.last) <= 0 {
		return false
	}
	s.seen, s.last = true, next
	return true
}

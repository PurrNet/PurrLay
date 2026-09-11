package main

import "sync"

// Producers cannot block Pion callbacks. Reliable overflow closes the
// connection instead of silently losing data.
type frameQueue struct {
	mu         sync.Mutex
	reliable   []frame
	unreliable []frame
	bytes      int
	maxFrames  int
	maxBytes   int
	wake       chan struct{}
}

func newFrameQueue() *frameQueue {
	return &frameQueue{maxFrames: 128, maxBytes: 1024 * 1024, wake: make(chan struct{}, 1)}
}

func (q *frameQueue) push(f frame) bool {
	q.mu.Lock()
	defer q.mu.Unlock()
	// Evict old snapshots before they displace reliable data.
	for len(q.unreliable) > 0 && (len(q.reliable)+len(q.unreliable) >= q.maxFrames || q.bytes+len(f.payload) > q.maxBytes || isUnreliable(f.delivery) && len(q.unreliable) >= 16) {
		q.bytes -= len(q.unreliable[0].payload)
		q.unreliable[0] = frame{}
		q.unreliable = q.unreliable[1:]
	}
	if len(q.reliable)+len(q.unreliable) >= q.maxFrames || q.bytes+len(f.payload) > q.maxBytes {
		return isUnreliable(f.delivery) // dropping an unreliable frame is allowed
	}
	q.bytes += len(f.payload)
	if isUnreliable(f.delivery) {
		q.unreliable = append(q.unreliable, f)
	} else {
		q.reliable = append(q.reliable, f)
	}
	select {
	case q.wake <- struct{}{}:
	default:
	}
	return true
}

func (q *frameQueue) pop() (frame, bool) {
	q.mu.Lock()
	defer q.mu.Unlock()
	var f frame
	if len(q.reliable) > 0 {
		f = q.reliable[0]
		q.reliable[0] = frame{}
		q.reliable = q.reliable[1:]
	} else if len(q.unreliable) > 0 {
		f = q.unreliable[0]
		q.unreliable[0] = frame{}
		q.unreliable = q.unreliable[1:]
	} else {
		return frame{}, false
	}
	q.bytes -= len(f.payload)
	return f, true
}

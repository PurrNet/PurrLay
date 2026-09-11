package main

import (
	"context"
	"io"
	"log"
	"os"
	"os/signal"
	"syscall"
)

func main() {
	log.SetFlags(log.LstdFlags | log.LUTC)
	c, err := loadConfig()
	if err != nil {
		log.Printf("configuration: %v", err)
		os.Exit(1)
	}
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	watchParentPipe(os.Getenv("PURR_WEBRTC_PARENT_PIPE"), os.Stdin, cancel)
	if err = serve(ctx, c); err != nil {
		log.Printf("gateway stopped: %v", err)
		os.Exit(1)
	}
}

func watchParentPipe(marker string, input io.Reader, cancel context.CancelFunc) {
	if marker != "true" {
		return
	}
	go func() {
		// The pipe closes even when the supervising process is forcibly terminated.
		_, _ = io.Copy(io.Discard, input)
		cancel()
	}()
}

package main

import (
	"errors"
	"fmt"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

const (
	maxPayload         = 65535
	setupTimeout       = 20 * time.Second
	bridgeWriteTimeout = 10 * time.Second
)

type config struct {
	httpAddress   string
	bridgeAddress string
	bridgeToken   string
	bindAddress   string
	udpPort       int
	publicIPs     []string
	maxSessions   int
}

func envDefault(key, fallback string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return fallback
}

func loadConfig() (config, error) {
	c := config{
		httpAddress:   envDefault("WEBRTC_HTTP_ADDRESS", "127.0.0.1:8090"),
		bridgeAddress: envDefault("PURR_WEBRTC_BRIDGE_ADDRESS", "127.0.0.1:8091"),
		bridgeToken:   os.Getenv("PURR_WEBRTC_BRIDGE_TOKEN"),
		bindAddress:   os.Getenv("WEBRTC_BIND_ADDRESS"),
	}
	if c.bridgeToken == "" || len(c.bridgeToken) > maxPayload {
		return c, errors.New("PURR_WEBRTC_BRIDGE_TOKEN must be nonempty and at most 65535 bytes")
	}
	if err := requireLoopback(c.httpAddress); err != nil {
		return c, fmt.Errorf("WEBRTC_HTTP_ADDRESS: %w", err)
	}
	if err := requireLoopback(c.bridgeAddress); err != nil {
		return c, fmt.Errorf("PURR_WEBRTC_BRIDGE_ADDRESS: %w", err)
	}
	if c.bindAddress == "" {
		c.bindAddress = "0.0.0.0"
		if os.Getenv("FLY_APP_NAME") != "" {
			c.bindAddress = "fly-global-services"
		}
	}
	var err error
	c.udpPort, err = strconv.Atoi(envDefault("WEBRTC_UDP_PORT", "7779"))
	if err != nil || c.udpPort < 1 || c.udpPort > 65535 {
		return c, errors.New("WEBRTC_UDP_PORT must be between 1 and 65535")
	}
	c.maxSessions, err = strconv.Atoi(envDefault("WEBRTC_MAX_SESSIONS", "1024"))
	if err != nil || c.maxSessions < 1 || c.maxSessions > 65535 {
		return c, errors.New("WEBRTC_MAX_SESSIONS must be between 1 and 65535")
	}
	if v := os.Getenv("WEBRTC_PUBLIC_IP"); v != "" {
		for _, raw := range strings.Split(v, ",") {
			ip := net.ParseIP(strings.TrimSpace(raw))
			if ip == nil || ip.To4() == nil {
				return c, errors.New("WEBRTC_PUBLIC_IP must contain comma-separated IPv4 addresses")
			}
			c.publicIPs = append(c.publicIPs, ip.To4().String())
		}
	}
	return c, nil
}

// Use numeric loopback addresses to avoid accidentally exposing the private API
// through DNS or a wildcard bind.
func requireLoopback(address string) error {
	host, port, err := net.SplitHostPort(address)
	if err != nil {
		return err
	}
	ip := net.ParseIP(host)
	if ip == nil || !ip.IsLoopback() {
		return errors.New("must use a numeric loopback IP address")
	}
	n, err := strconv.Atoi(port)
	if err != nil || n < 1 || n > 65535 {
		return errors.New("invalid port")
	}
	return nil
}

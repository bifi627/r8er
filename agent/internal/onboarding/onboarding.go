// Package onboarding is flow 1 of the pairing design: spend a one-time
// enrollment token for a long-lived credential, then hold the authenticated
// control socket and heartbeat (design: specs/2026-11-01-r8er-agent-pairing-design.md).
package onboarding

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
)

const heartbeatEvery = 30 * time.Second

// State is agent.json in the state dir. Delete the dir = brand-new agent.
type State struct {
	DeviceID   string `json:"deviceId"`
	Credential string `json:"credential"`
	Name       string `json:"name"`
}

// LoadOrEnroll returns the stored credential, or spends the enrollment token to
// get one. An existing credential wins: a token left in shell history or
// `docker inspect` is already used and harmless.
func LoadOrEnroll(ctx context.Context, dir, server, token, fp string) (*State, error) {
	path := filepath.Join(dir, "agent.json")
	if b, err := os.ReadFile(path); err == nil {
		var st State
		return &st, json.Unmarshal(b, &st)
	} else if !errors.Is(err, os.ErrNotExist) {
		return nil, err
	}
	if token == "" {
		return nil, errors.New("not enrolled: set R8ER_ENROLL_TOKEN (web app → Add agent)")
	}

	name, _ := os.Hostname()
	body, _ := json.Marshal(map[string]string{"token": token, "fingerprint": fp, "name": name})
	req, _ := http.NewRequestWithContext(ctx, http.MethodPost, server+"/agents/enroll", bytes.NewReader(body))
	req.Header.Set("Content-Type", "application/json")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		return nil, fmt.Errorf("enroll: %w", err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("enroll: %s (token expired or already used? create a new one)", res.Status)
	}
	st := State{Name: name}
	if err := json.NewDecoder(res.Body).Decode(&st); err != nil {
		return nil, fmt.Errorf("enroll: %w", err)
	}
	b, _ := json.Marshal(st)
	if err := os.WriteFile(path, b, 0o600); err != nil {
		return nil, err
	}
	log.Printf("agent: enrolled as device %s", st.DeviceID)
	return &st, nil
}

// RunControl holds the authenticated control socket and heartbeats until it
// drops. PR A: outbound heartbeats only; PR B adds signaling on this socket.
func RunControl(ctx context.Context, server string, st *State, lanURL string, online *atomic.Bool) error {
	conn, _, err := websocket.Dial(ctx, server+"/agents/ws", &websocket.DialOptions{
		HTTPHeader: http.Header{"Authorization": {"Bearer " + st.Credential}},
	})
	if err != nil {
		return fmt.Errorf("dial control: %w", err)
	}
	defer conn.CloseNow()
	defer online.Store(false)
	ctx = conn.CloseRead(ctx) // nothing inbound yet; this still handles pings/close

	hb, _ := json.Marshal(map[string]string{"type": "heartbeat", "lanUrl": lanURL})
	t := time.NewTicker(heartbeatEvery)
	defer t.Stop()
	for {
		if err := conn.Write(ctx, websocket.MessageText, hb); err != nil {
			return fmt.Errorf("control: %w", err)
		}
		online.Store(true)
		select {
		case <-ctx.Done():
			return fmt.Errorf("control closed: %w", ctx.Err())
		case <-t.C:
		}
	}
}

// LANURL is the page's address on the home LAN: R8ER_LAN_URL if set (needed in
// Docker, where the container IP is useless), else the first private IPv4.
func LANURL(port string) string {
	if u := os.Getenv("R8ER_LAN_URL"); u != "" {
		return u
	}
	addrs, _ := net.InterfaceAddrs()
	for _, a := range addrs {
		if n, ok := a.(*net.IPNet); ok && n.IP.To4() != nil && n.IP.IsPrivate() {
			return "http://" + net.JoinHostPort(n.IP.String(), port)
		}
	}
	return "http://" + net.JoinHostPort("localhost", port)
}

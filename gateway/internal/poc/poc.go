// Package poc is the Phase-0 POC session (steps 3+4), deleted by item 5: dial
// the room signaling server, answer a browser offer, and serve data channels. Two roles by label: "throughput" floods the
// channel to measure the gateway→browser ceiling (step 3); any other channel is
// an HTTP tunnel request — proxy it to the local origin (Jellyfin) and stream
// the response back in backpressured chunks (step 4, package tunnel).
// Throwaway quality: no auth, no tenancy.
package poc

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"net/http"
	"os"
	"strings"
	"time"

	"github.com/coder/websocket"
	"github.com/pion/webrtc/v4"

	"r8er/gateway/internal/tunnel"
)

const floodFor = 15 * time.Second // measurement window

// Msg is the signaling envelope (see docs/protocol.md). Hand-mirrored in
// dev/throughput.html until the protocol stabilizes and codegen earns its keep.
type Msg struct {
	Type      string                     `json:"type"` // "offer" | "answer" | "ice"
	SDP       *webrtc.SessionDescription `json:"sdp,omitempty"`
	Candidate *webrtc.ICECandidateInit   `json:"candidate,omitempty"`
}

// RunSession dials the signaling server and serves offers until the socket
// drops, returning the error that ended it. One PeerConnection per session: a
// PC is single-use (once the browser disconnects it can't answer a new offer),
// so build a fresh one on each offer, closing the previous. POC is one browser
// at a time. ponytail: no session map until there are many peers.
func RunSession(ctx context.Context, signalURL, origin string, client *http.Client, cert *webrtc.Certificate) error {
	conn, _, err := websocket.Dial(ctx, signalURL, nil)
	if err != nil {
		return fmt.Errorf("dial signaling: %w", err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(1 << 20) // SDP exceeds the 32KB default read limit

	send := func(m Msg) {
		b, _ := json.Marshal(m)
		if err := conn.Write(ctx, websocket.MessageText, b); err != nil {
			log.Printf("gateway: ws write: %v", err)
		}
	}

	// Keepalive: coder/websocket sends no automatic pings, so an idle session
	// gets reaped by Railway's edge (the 10-min-idle EOF). Ping periodically to
	// keep it alive; Ping needs the Read loop below to process the pong, so it
	// runs concurrently and stops when the session ends.
	sctx, cancel := context.WithCancel(ctx)
	defer cancel()
	go keepAlive(sctx, conn)

	var pc *webrtc.PeerConnection
	defer func() {
		if pc != nil {
			pc.Close()
		}
	}()

	for {
		_, data, err := conn.Read(ctx)
		if err != nil {
			return fmt.Errorf("ws closed: %w", err)
		}
		var m Msg
		if err := json.Unmarshal(data, &m); err != nil {
			log.Printf("gateway: bad message: %v", err)
			continue
		}
		switch m.Type {
		case "offer":
			if pc != nil {
				pc.Close()
			}
			var err error
			pc, err = newPeer(send, client, origin, cert)
			if err != nil {
				// A single bad offer must not take the gateway down — log and
				// wait for the next one. This runs on every browser connect.
				log.Printf("gateway: new peer: %v", err)
				continue
			}
			if err := answer(pc, m.SDP, send); err != nil {
				log.Printf("gateway: answer: %v", err)
			}
		case "ice":
			if pc != nil && m.Candidate != nil {
				if err := pc.AddICECandidate(*m.Candidate); err != nil {
					log.Printf("gateway: add ice: %v", err)
				}
			}
		}
	}
}

// keepAlive pings every 20s so an idle signaling socket isn't reaped by the
// edge proxy. A failed ping means the socket is already gone — return and let
// the Read loop surface the error and trigger a reconnect.
func keepAlive(ctx context.Context, conn *websocket.Conn) {
	t := time.NewTicker(20 * time.Second)
	defer t.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-t.C:
			pctx, cancel := context.WithTimeout(ctx, 10*time.Second)
			err := conn.Ping(pctx)
			cancel()
			if err != nil {
				log.Printf("gateway: keepalive ping failed: %v", err)
				return
			}
		}
	}
}

// newPeer builds the answering PeerConnection. Inbound data channels are
// dispatched by label: "throughput" -> flood; anything else -> HTTP tunnel.
func newPeer(send func(Msg), client *http.Client, origin string, cert *webrtc.Certificate) (*webrtc.PeerConnection, error) {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{
		ICEServers:   iceServers(),
		Certificates: []webrtc.Certificate{*cert}, // persistent: browsers pin its fingerprint
	})
	if err != nil {
		return nil, err
	}

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c == nil {
			return // gathering complete
		}
		init := c.ToJSON()
		send(Msg{Type: "ice", Candidate: &init})
	})

	pc.OnConnectionStateChange(func(st webrtc.PeerConnectionState) {
		log.Printf("gateway: connection state: %s", st)
	})

	pc.OnDataChannel(func(d *webrtc.DataChannel) {
		log.Printf("gateway: data channel %q opening", d.Label())
		if d.Label() == "throughput" {
			d.OnOpen(func() { flood(d) })
			return
		}
		tunnel.Serve(d, client, origin)
	})

	return pc, nil
}

func answer(pc *webrtc.PeerConnection, offer *webrtc.SessionDescription, send func(Msg)) error {
	if offer == nil {
		return fmt.Errorf("offer with nil sdp")
	}
	if err := pc.SetRemoteDescription(*offer); err != nil {
		return err
	}
	ans, err := pc.CreateAnswer(nil)
	if err != nil {
		return err
	}
	if err := pc.SetLocalDescription(ans); err != nil {
		return err
	}
	// Trickle: send the answer now, candidates follow as they gather.
	send(Msg{Type: "answer", SDP: pc.LocalDescription()})
	return nil
}

// flood sends fixed chunks for the measurement window and logs the send-side
// rate — the SCTP-limited throughput ceiling the browser confirms on receive.
func flood(d *webrtc.DataChannel) {
	buf := make([]byte, tunnel.ChunkSize)
	wait := tunnel.Gate(d)

	log.Printf("gateway: flooding %d-byte chunks for %s", tunnel.ChunkSize, floodFor)
	start := time.Now()
	deadline := start.Add(floodFor)
	var total int64
	for time.Now().Before(deadline) {
		wait()
		if err := d.Send(buf); err != nil {
			log.Printf("gateway: send: %v", err)
			break
		}
		total += int64(len(buf))
	}
	secs := time.Since(start).Seconds()
	log.Printf("gateway: sent %.1f MB in %.1fs = %.0f Mbps",
		float64(total)/1e6, secs, float64(total)*8/1e6/secs)
}

// iceServers returns Google STUN plus, when configured, a TURN server (POC
// step 6). TURN is opt-in via env so the default dev run stays cloud-free:
//
//	R8ER_ICE_URLS        comma-separated, e.g. "turn:turn.example:3478?transport=udp"
//	R8ER_ICE_USERNAME    TURN long-term username
//	R8ER_ICE_CREDENTIAL  TURN long-term credential
//
// Local coturn parity: URLS=turn:host.docker.internal:3478?transport=udp,
// USERNAME=dev, CREDENTIAL=dev. The browser is what's forced relay-only; the
// gateway just needs a relay candidate available, so STUN always stays present.
func iceServers() []webrtc.ICEServer {
	servers := []webrtc.ICEServer{{URLs: []string{"stun:stun.l.google.com:19302"}}}
	if urls := os.Getenv("R8ER_ICE_URLS"); urls != "" {
		servers = append(servers, webrtc.ICEServer{
			URLs:       strings.Split(urls, ","),
			Username:   os.Getenv("R8ER_ICE_USERNAME"),
			Credential: os.Getenv("R8ER_ICE_CREDENTIAL"),
		})
	}
	return servers
}

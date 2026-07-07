// r8er host agent — POC steps 3+4: dial the signaling server, answer a browser
// offer, and serve data channels. Two roles by label: "throughput" floods the
// channel to measure the agent→browser ceiling (step 3); any other channel is
// an HTTP tunnel request — proxy it to the local origin (Jellyfin) and stream
// the response back in backpressured chunks (step 4). Throwaway quality: no
// auth, no tenancy — just the transport and the tunnel primitive.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
	"github.com/pion/webrtc/v4"
)

// Msg is the signaling envelope (see docs/protocol.md). Hand-mirrored in
// dev/throughput.html until the protocol stabilizes and codegen earns its keep.
type Msg struct {
	Type      string                     `json:"type"` // "offer" | "answer" | "ice"
	SDP       *webrtc.SessionDescription `json:"sdp,omitempty"`
	Candidate *webrtc.ICECandidateInit   `json:"candidate,omitempty"`
}

const (
	chunkSize   = 16 * 1024        // ponytail: 16KB chunks per the data-channel rule
	floodFor    = 15 * time.Second // measurement window
	maxBuffered = 1 << 20          // 1MB send-buffer high-water mark (backpressure)
)

func main() {
	signalURL := env("R8ER_SIGNAL_URL", "wss://r8er.up.railway.app/signaling/poc")
	origin := env("R8ER_ORIGIN", "http://localhost:8096") // local Jellyfin
	log.Printf("agent: dialing %s (tunnel origin %s)", signalURL, origin)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	// No Client.Timeout: it caps the whole body read, which would abort a long
	// stream mid-movie. ponytail: POC leaks a goroutine on a hung origin; a
	// per-request context with a header-only deadline is the upgrade path.
	// DisableCompression: a tunnel must be byte-transparent. Go's default
	// transport auto-adds Accept-Encoding: gzip, then silently decompresses —
	// which rewrites the body and drops Content-Length. Forward exactly what the
	// browser asked for and hand back exactly what the origin sent.
	client := &http.Client{Transport: &http.Transport{DisableCompression: true}}

	// Reconnect loop. The signaling WS is long-lived and mostly idle (the owner
	// connects rarely), and Railway's edge reaps idle sockets — so the socket
	// WILL drop. A home agent must survive that unattended, so reconnect with
	// backoff instead of exiting. A session that stayed up a while resets the
	// backoff; a fast-failing dial keeps backing off up to 30s.
	backoff := time.Second
	for ctx.Err() == nil {
		start := time.Now()
		if err := runSession(ctx, signalURL, origin, client); err != nil {
			log.Printf("agent: %v", err)
		}
		if ctx.Err() != nil {
			return // Ctrl+C
		}
		if time.Since(start) > 30*time.Second {
			backoff = time.Second // the connection was healthy; treat this as a fresh drop
		}
		log.Printf("agent: reconnecting in %s", backoff)
		select {
		case <-ctx.Done():
			return
		case <-time.After(backoff):
		}
		backoff = min(backoff*2, 30*time.Second)
	}
}

// runSession dials the signaling server and serves offers until the socket
// drops, returning the error that ended it. One PeerConnection per session: a
// PC is single-use (once the browser disconnects it can't answer a new offer),
// so build a fresh one on each offer, closing the previous. POC is one browser
// at a time. ponytail: no session map until there are many peers.
func runSession(ctx context.Context, signalURL, origin string, client *http.Client) error {
	conn, _, err := websocket.Dial(ctx, signalURL, nil)
	if err != nil {
		return fmt.Errorf("dial signaling: %w", err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(1 << 20) // SDP exceeds the 32KB default read limit

	send := func(m Msg) {
		b, _ := json.Marshal(m)
		if err := conn.Write(ctx, websocket.MessageText, b); err != nil {
			log.Printf("agent: ws write: %v", err)
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
			log.Printf("agent: bad message: %v", err)
			continue
		}
		switch m.Type {
		case "offer":
			if pc != nil {
				pc.Close()
			}
			var err error
			pc, err = newPeer(send, client, origin)
			if err != nil {
				// A single bad offer must not take the agent down — log and
				// wait for the next one. This runs on every browser connect.
				log.Printf("agent: new peer: %v", err)
				continue
			}
			if err := answer(pc, m.SDP, send); err != nil {
				log.Printf("agent: answer: %v", err)
			}
		case "ice":
			if pc != nil && m.Candidate != nil {
				if err := pc.AddICECandidate(*m.Candidate); err != nil {
					log.Printf("agent: add ice: %v", err)
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
				log.Printf("agent: keepalive ping failed: %v", err)
				return
			}
		}
	}
}

// newPeer builds the answering PeerConnection. Inbound data channels are
// dispatched by label: "throughput" -> flood; anything else -> HTTP tunnel.
func newPeer(send func(Msg), client *http.Client, origin string) (*webrtc.PeerConnection, error) {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{
		ICEServers: iceServers(),
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
		log.Printf("agent: connection state: %s", st)
	})

	pc.OnDataChannel(func(d *webrtc.DataChannel) {
		log.Printf("agent: data channel %q opening", d.Label())
		if d.Label() == "throughput" {
			d.OnOpen(func() { flood(d) })
			return
		}
		serveTunnel(d, client, origin)
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

// gate returns a wait() to call before each Send. It blocks until the channel's
// SCTP send buffer has drained below the low-water mark, so the producer can't
// queue gigabytes ahead of the ~tens-of-MB/s drain (the 20GB-RAM incident).
func gate(d *webrtc.DataChannel) func() {
	drained := make(chan struct{}, 1)
	d.SetBufferedAmountLowThreshold(maxBuffered / 2)
	d.OnBufferedAmountLow(func() {
		select {
		case drained <- struct{}{}:
		default:
		}
	})
	return func() {
		if d.BufferedAmount() > maxBuffered {
			<-drained
		}
	}
}

// flood sends fixed chunks for the measurement window and logs the send-side
// rate — the SCTP-limited throughput ceiling the browser confirms on receive.
func flood(d *webrtc.DataChannel) {
	buf := make([]byte, chunkSize)
	wait := gate(d)

	log.Printf("agent: flooding %d-byte chunks for %s", chunkSize, floodFor)
	start := time.Now()
	deadline := start.Add(floodFor)
	var total int64
	for time.Now().Before(deadline) {
		wait()
		if err := d.Send(buf); err != nil {
			log.Printf("agent: send: %v", err)
			break
		}
		total += int64(len(buf))
	}
	secs := time.Since(start).Seconds()
	log.Printf("agent: sent %.1f MB in %.1fs = %.0f Mbps",
		float64(total)/1e6, secs, float64(total)*8/1e6/secs)
}

// tunnelReq is the browser→agent request frame (docs/protocol.md). Body-less:
// the POC only tunnels GETs (playlists, segments, ranged reads).
type tunnelReq struct {
	Method  string            `json:"method"`
	URL     string            `json:"url"` // origin-relative, e.g. "/web/index.html"
	Headers map[string]string `json:"headers"`
}

// serveTunnel treats the channel's first message as an HTTP request, proxies it
// to origin, and streams the response back. One channel == one request/response;
// closing the channel is the body's EOF marker for the browser.
func serveTunnel(d *webrtc.DataChannel, client *http.Client, origin string) {
	var once sync.Once
	d.OnMessage(func(m webrtc.DataChannelMessage) {
		once.Do(func() { go handleReq(d, client, origin, m.Data) })
	})
}

func handleReq(d *webrtc.DataChannel, client *http.Client, origin string, raw []byte) {
	defer d.Close()

	status, hdr, body, err := proxy(client, origin, raw)
	if err != nil {
		log.Printf("agent: tunnel: %v", err)
		sendHead(d, http.StatusBadGateway, nil)
		return
	}
	defer body.Close()

	sendHead(d, status, hdr)
	streamBody(d, body)
	log.Printf("agent: tunnel -> %d", status)
}

// proxy is the tunnel's pure core: decode the request frame, call origin
// forwarding headers (Range included, so seek survives), and hand back the
// response status/headers/body for the caller to stream. No WebRTC here, so
// range passthrough is unit-tested directly (tunnel_test.go).
func proxy(client *http.Client, origin string, raw []byte) (int, map[string]string, io.ReadCloser, error) {
	var rq tunnelReq
	if err := json.Unmarshal(raw, &rq); err != nil {
		return 0, nil, nil, fmt.Errorf("bad request frame: %w", err)
	}
	req, err := http.NewRequest(rq.Method, origin+rq.URL, nil)
	if err != nil {
		return 0, nil, nil, err
	}
	for k, v := range rq.Headers {
		req.Header.Set(k, v)
	}
	resp, err := client.Do(req)
	if err != nil {
		return 0, nil, nil, err
	}
	hdr := make(map[string]string, len(resp.Header)+1)
	for k := range resp.Header {
		hdr[k] = resp.Header.Get(k) // ponytail: last value wins; POC has no dup headers that matter
	}
	// Go's Transport promotes Content-Length out of Header into resp.ContentLength;
	// re-add it so the browser (and hls.js) knows the body size. -1 = unknown.
	if resp.ContentLength >= 0 {
		hdr["Content-Length"] = strconv.FormatInt(resp.ContentLength, 10)
	}
	return resp.StatusCode, hdr, resp.Body, nil
}

func sendHead(d *webrtc.DataChannel, status int, headers map[string]string) {
	b, _ := json.Marshal(map[string]any{"status": status, "headers": headers})
	if err := d.SendText(string(b)); err != nil {
		log.Printf("agent: send head: %v", err)
	}
}

// streamBody relays the response body as backpressured binary chunks. The head
// frame is text; body frames are binary — the browser tells them apart by type.
func streamBody(d *webrtc.DataChannel, body io.Reader) {
	buf := make([]byte, chunkSize)
	wait := gate(d)
	for {
		n, err := body.Read(buf)
		if n > 0 {
			wait()
			if e := d.Send(buf[:n]); e != nil {
				log.Printf("agent: tunnel send body: %v", e)
				return
			}
		}
		if err != nil {
			return // io.EOF (or read error) → defer closes the channel = EOF
		}
	}
}

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
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
// agent just needs a relay candidate available, so STUN always stays present.
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

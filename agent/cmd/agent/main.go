// r8er host agent — POC step 3: dial the signaling server, answer a browser
// offer, open a WebRTC data channel, and flood it to measure raw agent→browser
// throughput (the media direction that bounds streaming). Throwaway quality:
// no auth, no tenancy, no Jellyfin yet — just the transport ceiling.
package main

import (
	"context"
	"encoding/json"
	"fmt"
	"log"
	"os"
	"os/signal"
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
	log.Printf("agent: dialing %s", signalURL)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	conn, _, err := websocket.Dial(ctx, signalURL, nil)
	if err != nil {
		log.Fatalf("agent: dial signaling: %v", err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(1 << 20) // SDP exceeds the 32KB default read limit

	send := func(m Msg) {
		b, _ := json.Marshal(m)
		if err := conn.Write(ctx, websocket.MessageText, b); err != nil {
			log.Printf("agent: ws write: %v", err)
		}
	}

	pc := newPeer(send)
	defer pc.Close()

	for {
		_, data, err := conn.Read(ctx)
		if err != nil {
			log.Printf("agent: ws closed: %v", err)
			return
		}
		var m Msg
		if err := json.Unmarshal(data, &m); err != nil {
			log.Printf("agent: bad message: %v", err)
			continue
		}
		switch m.Type {
		case "offer":
			if err := answer(pc, m.SDP, send); err != nil {
				log.Printf("agent: answer: %v", err)
			}
		case "ice":
			if m.Candidate != nil {
				if err := pc.AddICECandidate(*m.Candidate); err != nil {
					log.Printf("agent: add ice: %v", err)
				}
			}
		}
	}
}

// newPeer builds the answering PeerConnection with detached data channels so
// the flood loop gets a raw io.Writer (blocking writes apply SCTP backpressure).
func newPeer(send func(Msg)) *webrtc.PeerConnection {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{{URLs: []string{"stun:stun.l.google.com:19302"}}},
	})
	if err != nil {
		log.Fatalf("agent: new peer: %v", err)
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
		d.OnOpen(func() { flood(d) })
	})

	return pc
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
// Backpressure: pause when the SCTP send buffer exceeds maxBuffered, resume
// when OnBufferedAmountLow fires. Without this the loop queues gigabytes into
// the send buffer (the producer far outpaces the ~tens-of-MB/s SCTP drain).
func flood(d *webrtc.DataChannel) {
	buf := make([]byte, chunkSize)
	drained := make(chan struct{}, 1)
	d.SetBufferedAmountLowThreshold(maxBuffered / 2)
	d.OnBufferedAmountLow(func() {
		select {
		case drained <- struct{}{}:
		default:
		}
	})

	log.Printf("agent: flooding %d-byte chunks for %s", chunkSize, floodFor)
	start := time.Now()
	deadline := start.Add(floodFor)
	var total int64
	for time.Now().Before(deadline) {
		if d.BufferedAmount() > maxBuffered {
			<-drained // wait for the buffer to drain below the low threshold
			continue
		}
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

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

// Package sessions serves browsers brokered over the control socket (design:
// specs/2026-11-01-r8er-agent-pairing-design.md, "Concurrent sessions" and
// "Connection gating"): one PeerConnection per broker session, an `auth` data
// channel that proves the browser's client key bound to this DTLS session, and
// tunnel channels only once that key is on the allowlist — or gets approved.
package sessions

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"log"
	"os"
	"strings"
	"sync"
	"sync/atomic"

	"github.com/pion/webrtc/v4"

	"r8er/gateway/internal/identity"
	"r8er/gateway/internal/pairing"
	"r8er/gateway/internal/tunnel"
)

// Msg is the broker envelope on the control socket (docs/protocol.md).
type Msg struct {
	Type      string                     `json:"type"` // offer | answer | ice | bye
	Session   string                     `json:"session"`
	SDP       *webrtc.SessionDescription `json:"sdp,omitempty"`
	Candidate *webrtc.ICECandidateInit   `json:"candidate,omitempty"`
}

type Sessions struct {
	Cert     *webrtc.Certificate
	Fp       string // our DTLS fingerprint
	Store    *pairing.Store
	Jellyfin *tunnel.Jellyfin // the tunnel's origin + policy

	mu  sync.Mutex
	pcs map[string]*webrtc.PeerConnection
}

// Handle processes one broker message; send writes JSON to the current control socket.
func (s *Sessions) Handle(raw []byte, send func(any)) {
	var m Msg
	if json.Unmarshal(raw, &m) != nil || m.Session == "" {
		return // heartbeat acks etc.: not ours
	}
	s.mu.Lock()
	if s.pcs == nil {
		s.pcs = map[string]*webrtc.PeerConnection{}
	}
	pc := s.pcs[m.Session]
	s.mu.Unlock()

	switch m.Type {
	case "offer":
		if pc != nil || m.SDP == nil {
			return
		}
		pc, err := s.newPeer(m.Session, send)
		if err != nil {
			log.Printf("gateway: session %s: %v", m.Session, err)
			return
		}
		if err := answer(pc, *m.SDP, m.Session, send); err != nil {
			log.Printf("gateway: session %s: answer: %v", m.Session, err)
			pc.Close()
		}
	case "ice":
		if pc != nil && m.Candidate != nil {
			pc.AddICECandidate(*m.Candidate)
		}
	case "bye":
		if pc != nil {
			pc.Close()
		}
	}
}

func (s *Sessions) newPeer(id string, send func(any)) (*webrtc.PeerConnection, error) {
	pc, err := webrtc.NewPeerConnection(webrtc.Configuration{
		ICEServers:   iceServers(),
		Certificates: []webrtc.Certificate{*s.Cert}, // persistent: browsers pin it
	})
	if err != nil {
		return nil, err
	}
	s.mu.Lock()
	s.pcs[id] = pc
	s.mu.Unlock()

	pc.OnICECandidate(func(c *webrtc.ICECandidate) {
		if c != nil {
			init := c.ToJSON()
			send(Msg{Type: "ice", Session: id, Candidate: &init})
		}
	})
	pc.OnConnectionStateChange(func(st webrtc.PeerConnectionState) {
		log.Printf("gateway: session %s: %s", id, st)
		if st == webrtc.PeerConnectionStateFailed || st == webrtc.PeerConnectionStateClosed ||
			st == webrtc.PeerConnectionStateDisconnected {
			pc.Close()
			s.mu.Lock()
			delete(s.pcs, id)
			s.mu.Unlock()
		}
	})

	var authed atomic.Bool
	pc.OnDataChannel(func(d *webrtc.DataChannel) {
		switch {
		case d.Label() == "auth":
			s.serveAuth(pc, d, &authed)
		case authed.Load():
			tunnel.Serve(d, s.Jellyfin)
		default:
			d.OnOpen(func() { d.Close() }) // not paired: nothing but `auth`
		}
	})
	return pc, nil
}

// authMsg is every message on the `auth` channel (docs/protocol.md, "Pairing").
type authMsg struct {
	Type string `json:"type"`
	N    string `json:"n,omitempty"`    // challenge / nonce / reveal
	Spki []byte `json:"spki,omitempty"` // base64 in JSON
	Sig  []byte `json:"sig,omitempty"`
	H    string `json:"h,omitempty"` // commit
	Name string `json:"name,omitempty"`
	Pair bool   `json:"pair,omitempty"`   // auth: browser has no matching pin → force the code comparison
	Why  string `json:"reason,omitempty"` // rejected
}

// serveAuth runs challenge → auth → (ok | unpaired → commit → nonce → reveal →
// approved/rejected). Messages on one channel arrive in order, one at a time.
func (s *Sessions) serveAuth(pc *webrtc.PeerConnection, d *webrtc.DataChannel, authed *atomic.Bool) {
	say := func(m authMsg) { b, _ := json.Marshal(m); d.SendText(string(b)) }
	fail := func(why string) {
		log.Printf("gateway: auth: %s", why)
		say(authMsg{Type: "rejected", Why: why})
		d.Close()
	}
	challenge := pairing.Nonce()
	var keyFp, commit, name, nA string

	d.OnOpen(func() { say(authMsg{Type: "challenge", N: challenge}) })
	d.OnMessage(func(raw webrtc.DataChannelMessage) {
		var m authMsg
		if json.Unmarshal(raw.Data, &m) != nil {
			fail("bad message")
			return
		}
		switch {
		case m.Type == "auth" && keyFp == "":
			// The browser's DTLS cert as actually negotiated on this connection.
			browserFp := identity.Fingerprint(pc.SCTP().Transport().GetRemoteCertificate())
			fp, err := pairing.VerifyAuth(m.Spki, m.Sig, s.Fp, browserFp, challenge)
			if err != nil {
				fail(err.Error())
				return
			}
			keyFp = fp
			if s.Store.Allowed(fp) && !m.Pair {
				authed.Store(true)
				say(authMsg{Type: "ok"})
			} else {
				say(authMsg{Type: "unpaired"})
			}
		case m.Type == "commit" && keyFp != "" && !authed.Load() && commit == "":
			commit, name = m.H, trim(m.Name)
			nA = pairing.Nonce() // only now: the browser is committed to nB
			say(authMsg{Type: "nonce", N: nA})
		case m.Type == "reveal" && commit != "" && nA != "":
			h := sha256.Sum256([]byte(m.N))
			if hex.EncodeToString(h[:]) != commit {
				fail("reveal does not match commit")
				return
			}
			code := pairing.Code(s.Fp, keyFp, nA, m.N)
			nA = "" // one reveal per channel
			go func() {
				ok, err := s.Store.Ask(name, keyFp, code)
				switch {
				case err != nil:
					fail(err.Error())
				case !ok:
					fail("not approved")
				default:
					authed.Store(true)
					say(authMsg{Type: "approved"})
				}
			}()
		default:
			fail("unexpected " + m.Type)
		}
	})
}

func trim(name string) string {
	name = strings.TrimSpace(name)
	if len(name) > 60 {
		name = name[:60]
	}
	if name == "" {
		return "browser"
	}
	return name
}

func answer(pc *webrtc.PeerConnection, offer webrtc.SessionDescription, id string, send func(any)) error {
	if err := pc.SetRemoteDescription(offer); err != nil {
		return err
	}
	ans, err := pc.CreateAnswer(nil)
	if err != nil {
		return err
	}
	if err := pc.SetLocalDescription(ans); err != nil {
		return err
	}
	send(Msg{Type: "answer", Session: id, SDP: pc.LocalDescription()})
	return nil
}

// iceServers: Google STUN plus optional TURN from env (R8ER_ICE_URLS,
// R8ER_ICE_USERNAME, R8ER_ICE_CREDENTIAL).
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

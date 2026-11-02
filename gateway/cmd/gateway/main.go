// r8er gateway. Wiring only — each feature lives in its own package under
// internal/ (vertical slices):
//
//	identity    persistent DTLS certificate + fingerprint (browsers pin it)
//	onboarding  enrollment token → credential, control socket + heartbeat
//	pairing     client-key auth, comparison code, allowlist + pending approvals
//	sessions    brokered browser sessions: auth channel, gated tunnel
//	localpage   the gateway's page on the home LAN (approve / remove browsers)
//	tunnel      HTTP over data channels (the streaming primitive)
//	poc         Phase-0 room signaling session (deleted by item 5)
//
// Config (env): R8ER_API_URL, R8ER_ENROLL_TOKEN, R8ER_STATE_DIR,
// R8ER_PAGE_PORT, R8ER_LAN_URL, R8ER_ORIGIN; POC: R8ER_SIGNAL_URL, R8ER_ICE_*.
package main

import (
	"context"
	"log"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"strings"
	"sync/atomic"
	"time"

	"r8er/gateway/internal/identity"
	"r8er/gateway/internal/localpage"
	"r8er/gateway/internal/onboarding"
	"r8er/gateway/internal/pairing"
	"r8er/gateway/internal/poc"
	"r8er/gateway/internal/sessions"
)

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	stateDir := env("R8ER_STATE_DIR", "r8er-state")
	cert, fp, err := identity.LoadOrCreate(stateDir)
	if err != nil {
		log.Fatalf("gateway: certificate: %v", err)
	}
	log.Printf("gateway: state %s, DTLS fingerprint %s", stateDir, fp)

	// No Client.Timeout: it caps the whole body read, which would abort a long
	// stream mid-movie. ponytail: POC leaks a goroutine on a hung origin; a
	// per-request context with a header-only deadline is the upgrade path.
	// DisableCompression: a tunnel must be byte-transparent. Go's default
	// transport auto-adds Accept-Encoding: gzip, then silently decompresses —
	// which rewrites the body and drops Content-Length. Forward exactly what the
	// browser asked for and hand back exactly what the origin sent.
	client := &http.Client{Transport: &http.Transport{DisableCompression: true}}
	origin := env("R8ER_ORIGIN", "http://localhost:8096") // local Jellyfin

	// MVP onboarding: enroll once, then heartbeat over the authenticated control
	// socket and serve the local page.
	api := strings.TrimRight(os.Getenv("R8ER_API_URL"), "/")
	if api != "" {
		st, err := onboarding.LoadOrEnroll(ctx, stateDir, api, os.Getenv("R8ER_ENROLL_TOKEN"), fp)
		if err != nil {
			log.Fatalf("gateway: %v", err)
		}
		log.Printf("gateway: id %s (%s)", st.GatewayID, st.Name)
		port := env("R8ER_PAGE_PORT", "8097") // Jellyfin owns 8096
		lan := onboarding.LANURL(port)
		lanHost := ""
		if u, err := url.Parse(lan); err == nil {
			lanHost = u.Hostname()
		}
		store, err := pairing.Open(stateDir)
		if err != nil {
			log.Fatalf("gateway: allowlist: %v", err)
		}
		sess := &sessions.Sessions{Cert: cert, Fp: fp, Store: store, Client: client, Origin: origin}
		var online atomic.Bool
		go func() {
			log.Printf("gateway: local page on %s", lan)
			page := localpage.Handler(st.Name, st.GatewayID, fp, &online, store)
			log.Fatal(http.ListenAndServe(":"+port, localpage.Guard(lanHost, page)))
		}()
		go forever(ctx, func(ctx context.Context) error {
			return onboarding.RunControl(ctx, api, st, lan, &online, sess.Handle)
		})
	}

	// POC room signaling (until item 5 replaces it): runs when no control plane
	// is configured, or when R8ER_SIGNAL_URL is set explicitly alongside it.
	if signalURL := os.Getenv("R8ER_SIGNAL_URL"); api == "" || signalURL != "" {
		signalURL = env("R8ER_SIGNAL_URL", "wss://r8er.up.railway.app/signaling/poc")
		log.Printf("gateway: dialing %s (tunnel origin %s)", signalURL, origin)
		go forever(ctx, func(ctx context.Context) error {
			return poc.RunSession(ctx, signalURL, origin, client, cert)
		})
	}
	<-ctx.Done()
}

// forever reruns fn until ctx ends. The API sockets are long-lived and
// mostly idle, and Railway's edge reaps idle sockets — so they WILL drop. A
// home gateway must survive that unattended, so reconnect with backoff instead
// of exiting. A session that stayed up a while resets the backoff; a
// fast-failing dial keeps backing off up to 30s.
func forever(ctx context.Context, fn func(context.Context) error) {
	backoff := time.Second
	for ctx.Err() == nil {
		start := time.Now()
		if err := fn(ctx); err != nil {
			log.Printf("gateway: %v", err)
		}
		if ctx.Err() != nil {
			return // Ctrl+C
		}
		if time.Since(start) > 30*time.Second {
			backoff = time.Second // the connection was healthy; treat this as a fresh drop
		}
		log.Printf("gateway: reconnecting in %s", backoff)
		select {
		case <-ctx.Done():
			return
		case <-time.After(backoff):
		}
		backoff = min(backoff*2, 30*time.Second)
	}
}

func env(k, def string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return def
}

// r8er gateway. Wiring only — each feature lives in its own package under
// internal/ (vertical slices):
//
//	identity    persistent DTLS certificate + fingerprint (browsers pin it)
//	onboarding  enrollment token → credential, control socket + heartbeat
//	pairing     client-key auth, comparison code, allowlist + pending approvals
//	sessions    brokered browser sessions: auth channel, gated tunnel
//	localpage   the gateway's page on the home LAN (approve / remove browsers)
//	tunnel      HTTP over data channels to Jellyfin (allowlist, key injection)
//
// Config (env): R8ER_API_URL (required), R8ER_ENROLL_TOKEN, R8ER_STATE_DIR,
// R8ER_PAGE_PORT, R8ER_LAN_URL, R8ER_ORIGIN, R8ER_JELLYFIN_API_KEY,
// R8ER_JELLYFIN_USER, R8ER_ICE_* (R8ER_ICE_PORT: Docker, see sessions.NewAPI).
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
	"r8er/gateway/internal/sessions"
	"r8er/gateway/internal/tunnel"
)

// version is set at build time (-ldflags -X main.version=…; see Dockerfile).
var version = "dev"

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()

	stateDir := env("R8ER_STATE_DIR", "r8er-state")
	cert, fp, err := identity.LoadOrCreate(stateDir)
	if err != nil {
		log.Fatalf("gateway: certificate: %v", err)
	}
	log.Printf("gateway: %s, state %s, DTLS fingerprint %s", version, stateDir, fp)

	// No Client.Timeout: it caps the whole body read, which would abort a long
	// stream mid-movie. ponytail: leaks a goroutine on a hung origin; a
	// per-request context with a header-only deadline is the upgrade path.
	// DisableCompression: a tunnel must be byte-transparent. Go's default
	// transport auto-adds Accept-Encoding: gzip, then silently decompresses —
	// which rewrites the body and drops Content-Length. Forward exactly what the
	// browser asked for and hand back exactly what the origin sent.
	jf := &tunnel.Jellyfin{
		Client: &http.Client{Transport: &http.Transport{DisableCompression: true}},
		URL:    env("R8ER_ORIGIN", "http://localhost:8096"), // local Jellyfin
		Key:    os.Getenv("R8ER_JELLYFIN_API_KEY"),
		User:   os.Getenv("R8ER_JELLYFIN_USER"),
	}
	if jf.Key == "" {
		log.Printf("gateway: R8ER_JELLYFIN_API_KEY not set — pairing works, playback refused (create a key in the Jellyfin dashboard)")
	}

	// Enroll once, then heartbeat over the authenticated control socket and
	// serve the local page.
	api := strings.TrimRight(os.Getenv("R8ER_API_URL"), "/")
	if api == "" {
		log.Fatal("gateway: R8ER_API_URL not set (use the install command from the web app)")
	}
	{
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
		webAPI, err := sessions.NewAPI()
		if err != nil {
			log.Fatalf("gateway: ICE: %v", err)
		}
		sess := &sessions.Sessions{Cert: cert, Fp: fp, Store: store, Jellyfin: jf, API: webAPI}
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

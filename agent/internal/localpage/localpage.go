// Package localpage is the agent's page on the home LAN (default :8097). PR B
// makes it the only place a device can be approved, so it refuses cross-site
// and DNS-rebinding requests from day one.
package localpage

import (
	"html/template"
	"net"
	"net/http"
	"strings"
	"sync/atomic"
)

// Guard protects the page. Host must be an IP literal, localhost, or the
// configured LAN host (blocks DNS rebinding); state-changing requests must come
// from the page's own origin (blocks drive-by approval from a malicious site).
func Guard(lanHost string, h http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		host := r.Host
		if hh, _, err := net.SplitHostPort(r.Host); err == nil {
			host = hh
		}
		if net.ParseIP(strings.Trim(host, "[]")) == nil && host != "localhost" && host != lanHost {
			http.Error(w, "bad host", http.StatusForbidden)
			return
		}
		if r.Method != http.MethodGet && r.Method != http.MethodHead && r.Header.Get("Origin") != "http://"+r.Host {
			http.Error(w, "cross-origin request refused", http.StatusForbidden)
			return
		}
		h.ServeHTTP(w, r)
	})
}

var page = template.Must(template.New("page").Parse(`<!doctype html>
<meta charset="utf-8"><meta name="viewport" content="width=device-width">
<title>r8er agent · {{.Name}}</title>
<body style="font-family:system-ui;max-width:40rem;margin:2rem auto;padding:0 1rem">
<h1>r8er agent · {{.Name}}</h1>
<p>Control plane: <b>{{if .Online}}online{{else}}offline{{end}}</b></p>
<p>Device id: <code>{{.DeviceID}}</code></p>
<p>DTLS fingerprint (browsers pin this):<br><code style="word-break:break-all">{{.Fingerprint}}</code></p>
</body>`))

// Handler renders the bare page. PR B adds pending approvals and the
// paired-device list.
func Handler(name, deviceID, fp string, online *atomic.Bool) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			http.NotFound(w, r)
			return
		}
		page.Execute(w, map[string]any{
			"Name": name, "DeviceID": deviceID, "Fingerprint": fp, "Online": online.Load(),
		})
	})
}

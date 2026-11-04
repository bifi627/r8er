// Package localpage is the gateway's page on the home LAN (default :8097): the
// only place a browser can be approved, so it refuses cross-site and
// DNS-rebinding requests.
package localpage

import (
	"html/template"
	"net"
	"net/http"
	"strings"
	"sync/atomic"

	"r8er/gateway/internal/pairing"
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
<meta http-equiv="refresh" content="3">
<title>r8er gateway · {{.Name}}</title>
<body style="font-family:system-ui;max-width:40rem;margin:2rem auto;padding:0 1rem">
<h1>r8er gateway · {{.Name}}</h1>
<p>Control plane: <b>{{if .Online}}online{{else}}offline{{end}}</b> · version <code>{{.Version}}</code></p>
{{with .Update}}<p style="border:2px solid #06c;padding:.5rem 1rem">Update available: <b>{{.}}</b></p>{{end}}
<p>Gateway id: <code>{{.GatewayID}}</code></p>
<h2>Pairing requests</h2>
{{range .Pending}}
<form method="post" action="/decide" style="border:2px solid #c80;padding:.5rem 1rem;margin:.5rem 0">
<p><b>{{.Name}}</b> wants to pair · code <b style="font-size:1.6em;letter-spacing:.1em">{{.Code}}</b></p>
<p>Approve only if your browser shows the same code.</p>
<input type="hidden" name="id" value="{{.ID}}">
<button name="approve" value="1">Approve</button> <button name="approve" value="0">Reject</button>
</form>
{{else}}<p>None. Press <i>Pair this browser</i> in the web app.</p>{{end}}
<h2>Paired browsers</h2>
{{range .Paired}}
<form method="post" action="/remove"><input type="hidden" name="keyFp" value="{{.KeyFp}}">
{{.Name}} <small>(since {{.AddedAt.Format "2006-01-02"}})</small> <button>Remove</button></form>
{{else}}<p>None yet.</p>{{end}}
<p><small>DTLS fingerprint (browsers pin this):<br><code style="word-break:break-all">{{.Fingerprint}}</code></small></p>
</body>`))

// Handler renders the page and takes the Approve/Reject/Remove posts. Only
// someone on the home LAN reaches it, and Guard stops other sites driving it.
// update holds the update notice ("" = up to date), set by the release check.
func Handler(name, gatewayID, fp, version string, online *atomic.Bool, update *atomic.Pointer[string], store *pairing.Store) http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /{$}", func(w http.ResponseWriter, r *http.Request) {
		page.Execute(w, map[string]any{
			"Name": name, "GatewayID": gatewayID, "Fingerprint": fp, "Online": online.Load(),
			"Version": version, "Update": *update.Load(),
			"Pending": store.Pending(), "Paired": store.Paired(),
		})
	})
	mux.HandleFunc("POST /decide", func(w http.ResponseWriter, r *http.Request) {
		if err := store.Decide(r.FormValue("id"), r.FormValue("approve") == "1"); err != nil {
			http.Error(w, err.Error(), http.StatusConflict)
			return
		}
		http.Redirect(w, r, "/", http.StatusSeeOther)
	})
	mux.HandleFunc("POST /remove", func(w http.ResponseWriter, r *http.Request) {
		if err := store.Remove(r.FormValue("keyFp")); err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		http.Redirect(w, r, "/", http.StatusSeeOther)
	})
	return mux
}

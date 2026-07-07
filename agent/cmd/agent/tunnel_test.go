package main

import (
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"bytes"
)

// The tunnel's whole reason to exist is that seek survives it: a Range request
// must come back as 206 with the right bytes. Prove that against a real HTTP
// server (http.ServeContent handles Range exactly like Jellyfin's static files),
// with no WebRTC in the loop — the channel plumbing is proven live in tunnel.html.
func TestProxyRangePassthrough(t *testing.T) {
	payload := bytes.Repeat([]byte("r8er-"), 400) // 2000 bytes, range-able
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		http.ServeContent(w, r, "x.bin", time.Time{}, bytes.NewReader(payload))
	}))
	defer srv.Close()

	// Full GET: 200, whole body, and Content-Length re-added (Go's Transport
	// promotes it off the Header map — the browser needs it back).
	status, hdr, body := do(t, srv.URL, tunnelReq{Method: "GET", URL: "/x.bin"})
	if status != 200 {
		t.Fatalf("full GET status = %d, want 200", status)
	}
	if !bytes.Equal(body, payload) {
		t.Fatalf("full body = %d bytes, want %d", len(body), len(payload))
	}
	if got := hdr["Content-Length"]; got != "2000" {
		t.Fatalf("Content-Length = %q, want \"2000\"", got)
	}

	// Ranged GET: 206, exactly the requested slice, Content-Range set.
	status, hdr, part := do(t, srv.URL, tunnelReq{
		Method: "GET", URL: "/x.bin",
		Headers: map[string]string{"Range": "bytes=0-99"},
	})
	if status != 206 {
		t.Fatalf("ranged GET status = %d, want 206", status)
	}
	if len(part) != 100 || !bytes.Equal(part, payload[:100]) {
		t.Fatalf("ranged body = %d bytes, want the first 100", len(part))
	}
	if hdr["Content-Range"] == "" {
		t.Fatalf("missing Content-Range header: %v", hdr)
	}
}

func do(t *testing.T, origin string, rq tunnelReq) (int, map[string]string, []byte) {
	t.Helper()
	raw, _ := json.Marshal(rq)
	status, hdr, body, err := proxy(http.DefaultClient, origin, raw)
	if err != nil {
		t.Fatalf("proxy: %v", err)
	}
	defer body.Close()
	b, _ := io.ReadAll(body)
	return status, hdr, b
}

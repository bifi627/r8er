package poc

import (
	"context"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"

	"r8er/gateway/internal/identity"
)

// When the signaling socket drops, RunSession must RETURN (so the reconnect
// loop can re-dial) — not hang and not exit the process. This is the crash the
// gateway hit: an idle socket got reaped and the old code fell out of main.
func TestRunSessionReturnsOnDrop(t *testing.T) {
	cert, _, err := identity.LoadOrCreate(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		c, err := websocket.Accept(w, r, nil)
		if err != nil {
			return
		}
		c.Close(websocket.StatusNormalClosure, "bye") // drop it immediately
	}))
	defer srv.Close()

	wsURL := "ws" + strings.TrimPrefix(srv.URL, "http")
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	if err := RunSession(ctx, wsURL, "http://unused", http.DefaultClient, cert); err == nil {
		t.Fatal("RunSession returned nil; want the socket-closed error that drives a reconnect")
	}
	if ctx.Err() != nil {
		t.Fatal("RunSession hung until ctx timeout instead of returning on the drop")
	}
}

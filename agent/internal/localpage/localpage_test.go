package localpage

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

// The page will approve devices (PR B): a malicious website must not be able
// to drive it, neither by cross-site POST nor by DNS rebinding.
func TestGuard(t *testing.T) {
	ok := http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {})
	h := Guard("nas.local", ok)
	cases := []struct {
		name, method, host, origin string
		want                       int
	}{
		{"LAN IP get", "GET", "192.168.1.20:8097", "", 200},
		{"localhost get", "GET", "localhost:8097", "", 200},
		{"configured host get", "GET", "nas.local:8097", "", 200},
		{"rebound attacker domain", "GET", "evil.example:8097", "", 403},
		{"same-origin post", "POST", "192.168.1.20:8097", "http://192.168.1.20:8097", 200},
		{"cross-origin post", "POST", "192.168.1.20:8097", "https://evil.example", 403},
		{"post without origin", "POST", "192.168.1.20:8097", "", 403},
	}
	for _, c := range cases {
		r := httptest.NewRequest(c.method, "/", nil)
		r.Host = c.host
		if c.origin != "" {
			r.Header.Set("Origin", c.origin)
		}
		w := httptest.NewRecorder()
		h.ServeHTTP(w, r)
		if w.Code != c.want {
			t.Errorf("%s: status %d, want %d", c.name, w.Code, c.want)
		}
	}
}

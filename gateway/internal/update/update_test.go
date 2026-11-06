package update

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestNewer(t *testing.T) {
	for _, c := range []struct {
		a, b string
		want bool
	}{
		{"1.10.0", "1.9.3", true},
		{"1.4.0", "1.4.0", false},
		{"1.3.9", "1.4.0", false},
		{"2.0.0", "1.99.99", true},
		{"1.5.0-nightly.3", "1.4.0", true},
		{"1.4.0", "dev", false}, // dev builds never "need" an update
		{"garbage", "1.0.0", false},
	} {
		if got := Newer(c.a, c.b); got != c.want {
			t.Errorf("Newer(%q, %q) = %v, want %v", c.a, c.b, got, c.want)
		}
	}
}

func TestLatest(t *testing.T) {
	tag := "gateway-v1.5.0"
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte(`{"tag_name":"` + tag + `"}`))
	}))
	defer srv.Close()
	if v, err := Latest(context.Background(), srv.URL); err != nil || v != "1.5.0" {
		t.Fatalf("Latest = %q, %v", v, err)
	}
	tag = "web-v3.0.0" // not a gateway release
	if v, err := Latest(context.Background(), srv.URL); err != nil || v != "" {
		t.Fatalf("Latest(non-gateway) = %q, %v", v, err)
	}
}

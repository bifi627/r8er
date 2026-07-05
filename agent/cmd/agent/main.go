// r8er host agent — runs on customer hardware, tunnels a local Jellyfin
// over a WebRTC data channel. Scaffold only; see docs/superpowers/specs/.
package main

import (
	"encoding/json"
	"log"
	"net/http"
)

func main() {
	// ponytail: local status endpoint only; signaling + Pion tunnel land in POC step 3
	http.HandleFunc("/status", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		json.NewEncoder(w).Encode(map[string]string{"status": "ok", "component": "r8er-agent"})
	})
	log.Println("r8er agent scaffold listening on 127.0.0.1:8090")
	log.Fatal(http.ListenAndServe("127.0.0.1:8090", nil))
}

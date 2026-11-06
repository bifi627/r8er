// Package update tells the user when a newer gateway release exists (item 4
// design: notify-only for dogfood). It never downloads or replaces anything:
// the install script does that when the user runs the printed command.
package update

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"runtime"
	"strconv"
	"strings"
)

// LatestURL is GitHub's latest *non-prerelease* release. ponytail: assumes
// every release in the repo is a gateway release (tag gateway-vX.Y.Z); filter
// by tag prefix over /releases if the repo ever publishes other releases.
const LatestURL = "https://api.github.com/repos/bifi627/streamiq/releases/latest"

// Latest returns the newest release's version ("1.4.0"), or "" if the latest
// release isn't a gateway release.
func Latest(ctx context.Context, url string) (string, error) {
	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("Accept", "application/vnd.github+json")
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		return "", err
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		return "", fmt.Errorf("release check: HTTP %d", res.StatusCode)
	}
	var rel struct {
		Tag string `json:"tag_name"`
	}
	if err := json.NewDecoder(res.Body).Decode(&rel); err != nil {
		return "", err
	}
	v, ok := strings.CutPrefix(rel.Tag, "gateway-v")
	if !ok {
		return "", nil
	}
	return v, nil
}

// Newer reports whether version a is newer than b ("1.10.0" > "1.9.3").
// Prerelease suffixes ("-nightly.3") are ignored. Unparsable = not newer.
func Newer(a, b string) bool {
	pa, pb := parse(a), parse(b)
	if pa == nil || pb == nil {
		return false
	}
	for i := range 3 {
		if pa[i] != pb[i] {
			return pa[i] > pb[i]
		}
	}
	return false
}

func parse(v string) []int {
	v, _, _ = strings.Cut(v, "-")
	parts := strings.Split(v, ".")
	if len(parts) != 3 {
		return nil
	}
	out := make([]int, 3)
	for i, p := range parts {
		n, err := strconv.Atoi(p)
		if err != nil {
			return nil
		}
		out[i] = n
	}
	return out
}

// Command is the one line the user runs to update on this OS.
func Command() string {
	const dl = "https://github.com/bifi627/streamiq/releases/latest/download/"
	if runtime.GOOS == "windows" {
		return "irm " + dl + "install.ps1 | iex"
	}
	return "curl -fsSL " + dl + "install.sh | sh"
}

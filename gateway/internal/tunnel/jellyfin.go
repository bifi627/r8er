package tunnel

import (
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"path"
	"regexp"
	"strings"
	"sync"
)

// Jellyfin is the tunnel's only origin, plus the policy that keeps a paired
// browser a *viewer* (docs/protocol.md, "Jellyfin policy"):
//   - the browser never holds a Jellyfin credential: the gateway injects its API
//     key and strips any the browser sends; response text that echoes the key
//     (Jellyfin writes ApiKey= into subtitle playlist URLs) is redacted;
//   - an API key is admin, so only an allowlist of consumption endpoints passes;
//   - every request is pinned to the household's Jellyfin user (userId).
type Jellyfin struct {
	Client *http.Client
	URL    string // e.g. http://localhost:8096
	Key    string // R8ER_JELLYFIN_API_KEY
	User   string // R8ER_JELLYFIN_USER (name); "" = the only user

	mu     sync.Mutex
	userID string
}

const (
	id      = `[0-9a-fA-F-]{32,36}`
	MaxBody = 64 * 1024        // request bodies: small JSON only
	maxText = 16 * 1024 * 1024 // buffered (redacted) text responses
)

// allowed is the attack surface: add an endpoint here consciously. A refused
// request is a 403 + a gateway log line.
var allowed = []struct {
	method string
	re     *regexp.Regexp
}{
	{"GET", regexp.MustCompile(`^/System/Info/Public$`)},
	{"GET", regexp.MustCompile(`^/UserViews$`)},
	{"GET", regexp.MustCompile(`^/Items$`)},
	{"GET", regexp.MustCompile(`^/UserItems/Resume$`)},
	{"GET", regexp.MustCompile(`^/Items/` + id + `$`)},
	{"GET", regexp.MustCompile(`^/Shows/` + id + `/(Seasons|Episodes)$`)},
	{"GET", regexp.MustCompile(`^/Items/` + id + `/Images/[A-Za-z]+(/[0-9]+)?$`)},
	{"POST", regexp.MustCompile(`^/Items/` + id + `/PlaybackInfo$`)},
	{"POST", regexp.MustCompile(`^/UserItems/` + id + `/UserData$`)}, // resume position / played
	// Every GET under /Videos/{id}/ is a read: HLS playlists + segments, subtitles.
	{"GET", regexp.MustCompile(`^/Videos/` + id + `/[A-Za-z0-9._/-]+$`)},
}

// allow reports whether method+path is on the allowlist. path must already be
// clean: the caller rejects "..", "//" and percent-encoded paths.
func allow(method, p string) bool {
	for _, a := range allowed {
		if a.method == method && a.re.MatchString(p) {
			return true
		}
	}
	return false
}

// prepare turns a browser request frame into the origin request, or returns
// the HTTP status to refuse it with.
func (j *Jellyfin) prepare(rq tunnelReq) (*http.Request, int, error) {
	if j.Key == "" {
		return nil, http.StatusServiceUnavailable, fmt.Errorf("R8ER_JELLYFIN_API_KEY not set")
	}
	u, err := url.Parse(rq.URL)
	if err != nil || u.IsAbs() || u.RawPath != "" || u.Path != path.Clean(u.Path) ||
		!allow(rq.Method, u.Path) {
		return nil, http.StatusForbidden, fmt.Errorf("refused %s %s", rq.Method, rq.URL)
	}
	if len(rq.Body) > MaxBody {
		return nil, http.StatusRequestEntityTooLarge, fmt.Errorf("body %d bytes", len(rq.Body))
	}
	uid, err := j.user()
	if err != nil {
		return nil, http.StatusBadGateway, err
	}
	q := u.Query()
	for k := range q {
		switch strings.ToLower(k) {
		case "api_key", "apikey", "userid":
			q.Del(k)
		}
	}
	q.Set("userId", uid)
	u.RawQuery = q.Encode()

	var body io.Reader
	if rq.Body != "" {
		body = strings.NewReader(rq.Body)
	}
	req, err := http.NewRequest(rq.Method, j.URL+u.String(), body)
	if err != nil {
		return nil, http.StatusBadRequest, err
	}
	for k, v := range rq.Headers {
		switch lk := strings.ToLower(k); {
		case lk == "authorization", lk == "cookie", strings.HasPrefix(lk, "x-emby"),
			strings.HasPrefix(lk, "x-mediabrowser"):
			// the browser's credentials (if any) never reach Jellyfin
		default:
			req.Header.Set(k, v)
		}
	}
	if rq.Body != "" && req.Header.Get("Content-Type") == "" {
		req.Header.Set("Content-Type", "application/json")
	}
	req.Header.Set("Authorization", `MediaBrowser Token="`+j.Key+`"`)
	return req, 0, nil
}

// redact buffers text responses (JSON, playlists) and removes the API key.
// Media (video/*, image/*) streams through untouched.
func (j *Jellyfin) redact(hdr map[string]string, body io.ReadCloser) (io.ReadCloser, error) {
	ct := strings.ToLower(hdr["Content-Type"])
	if !strings.Contains(ct, "json") && !strings.Contains(ct, "mpegurl") && !strings.HasPrefix(ct, "text/") {
		return body, nil
	}
	defer body.Close()
	b, err := io.ReadAll(io.LimitReader(body, maxText+1))
	if err != nil {
		return nil, err
	}
	if len(b) > maxText {
		return nil, fmt.Errorf("text response over %d bytes", maxText)
	}
	b = bytes.ReplaceAll(b, []byte(j.Key), []byte("redacted"))
	hdr["Content-Length"] = fmt.Sprint(len(b))
	return io.NopCloser(bytes.NewReader(b)), nil
}

// user resolves (once) the household's Jellyfin user id. Lazy, so the gateway
// starts before Jellyfin is up; a failure is retried on the next request.
func (j *Jellyfin) user() (string, error) {
	j.mu.Lock()
	defer j.mu.Unlock()
	if j.userID != "" {
		return j.userID, nil
	}
	req, _ := http.NewRequest("GET", j.URL+"/Users", nil)
	req.Header.Set("Authorization", `MediaBrowser Token="`+j.Key+`"`)
	resp, err := j.Client.Do(req)
	if err != nil {
		return "", fmt.Errorf("jellyfin users: %w", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("jellyfin users: HTTP %d (check R8ER_JELLYFIN_API_KEY)", resp.StatusCode)
	}
	var users []struct{ Name, Id string }
	if err := json.NewDecoder(resp.Body).Decode(&users); err != nil {
		return "", fmt.Errorf("jellyfin users: %w", err)
	}
	for _, u := range users {
		if (j.User == "" && len(users) == 1) || strings.EqualFold(u.Name, j.User) {
			j.userID = u.Id
			return u.Id, nil
		}
	}
	return "", fmt.Errorf("jellyfin: %d users and none named %q; set R8ER_JELLYFIN_USER", len(users), j.User)
}

# StreamIQ — Streaming client (MVP item 5) — design

Date: 2026-11-02 · Status: approved (A/B split); PR A built. Corrections from
probing the pinned Jellyfin 12.4 are folded in below.

## Intent

Get from "a paired browser holds an authorized tunnel" (items 2+3) to "the
owner opens StreamIQ anywhere, browses their library, and watches". Consumption
only. **Functional, not pretty**: plain HTML elements and minimal CSS; the UI
gets refactored later. The POC player is not reused. It was throwaway: the
loader, player and UI are written fresh against the MVP tunnel (`connect.ts`,
`gateway/internal/tunnel`).

## Goals / non-goals

**Goals (acceptance criteria)**
1. From the Gateways list, an online, paired gateway has **Open**, which
   connects and shows the library.
2. Browse: library views → items (title + poster) → for series, seasons →
   episodes. Movies and episodes have **Play**.
3. Play through the tunnel with hls.js. Seek works. A subtitle track can be
   picked when the item has one.
4. **Resume**: playback starts at Jellyfin's saved position, the position is
   saved while playing and on exit, and "continue watching" items come first.
5. The browser never holds a Jellyfin credential. The gateway injects it and
   only forwards an **allowlist** of consumption endpoints.
6. **TURN** enabled: the API hands out TURN credentials, and the client uses them.
7. **Privacy indicator** in the player, from live `getStats()`: "end-to-end
   encrypted · direct · StreamIQ carried 0 bytes" or "relayed (encrypted, StreamIQ
   cannot decrypt) · N MB relayed".
8. **Connection telemetry** goes to a tenant-scoped EF table: direct vs relay,
   IPv4/v6, bytes, duration per session.
9. POC paths are deleted: `/signaling/{room}`, all of `Features/Poc` (the
   telemetry sink too, since its only producer was the harness page),
   `wwwroot/`, `gateway/internal/poc` + POC mode in `main.go`, `dev/*.html`, and
   the POC section of `docs/protocol.md`.

**Non-goals**: Jellyfin management (IDEAS.md "Media management"); search;
music/photos; multi-member watch state (tenant = household = one Jellyfin
user); offline/cached browsing; iOS native HLS (needs a Service Worker,
deferred per protocol.md); pretty UI; TURN credential scoping/rotation
(item 6).

## Decisions

### Jellyfin credential lives in the gateway (handover notes)
Config: `STREAMIQ_JELLYFIN_API_KEY` (real installs: created once in the Jellyfin
dashboard while item 4 is deferred; dev: `scripts/setup-jellyfin.sh`). The
gateway resolves the household user lazily on the first request and caches it:
`STREAMIQ_JELLYFIN_USER` (name), or the only user if there is just one, otherwise
`502`. On every tunnelled request the gateway **sets** `Authorization:
MediaBrowser Token="…"`, strips client-sent `Authorization`, `Cookie`,
`X-Emby-*`, `X-MediaBrowser-*` and `api_key`/`ApiKey`, and **overwrites**
`userId` with the household user. (12.4 dropped the `/Users/{id}/…` routes,
so the planned `/Users/me` rewrite became a query parameter.)

**Redaction (found while probing):** Jellyfin writes `ApiKey=<key>` into the
subtitle URLs inside its HLS playlists. The gateway buffers text responses
(JSON, `mpegurl`, `text/*`, at most 16 MB), replaces the key with `redacted`,
and recomputes `Content-Length`. Media streams through untouched.

### Gateway path allowlist (security, not optional)
An API key is admin. Without a filter, any paired client could delete
libraries or users. The gateway forwards only matching `method path` pairs and
returns `403` for everything else:

```
GET  /System/Info/Public         ("Test connection" diagnostic)
GET  /UserViews | /Items | /UserItems/Resume | /Items/{id}
GET  /Shows/{id}/Seasons | /Shows/{id}/Episodes
GET  /Items/{id}/Images/{type}[/{index}]
POST /Items/{id}/PlaybackInfo
POST /UserItems/{id}/UserData    (resume position, played)
GET  /Videos/{id}/…              (master.m3u8, variant playlists, segments, subtitles)
```
`/Sessions/Playing*` was dropped: Jellyfin accepts it from an API-key caller
(204) but saves nothing, so resume writes `UserData` directly.
These are Go regexes in one table, with a table-driven test. ponytail: this
is the attack surface. Each endpoint is added consciously, and a refused path
shows up as a `403` in the gateway log.

### Tunnel framing: request body
Resume and PlaybackInfo need `POST` with JSON. Add optional `"body"` (UTF-8
string) to the request frame. Bodies are small JSON, so one text frame is
enough, capped at 64 KB (the gateway rejects anything larger). This is a
contract change to `docs/protocol.md`. The tunnel framing moves out of the
POC section into the MVP section.

### Browser
- `tunnelFetch(session, method, path, {headers, body})` → `{status, headers, body: Uint8Array}`.
  New TS, one data channel per request (unchanged framing). JSON helpers on top.
- **hls.js** (new runtime dependency; justification: HLS demux into MSE,
  playlists and ABR. No browser API does this, and the custom-loader decision
  is recorded in protocol.md). A custom loader routes playlist, fragment and
  subtitle loads through `tunnelFetch`, using a sentinel origin that can't be
  fetched directly.
- Posters: `tunnelFetch` → `Blob` → `URL.createObjectURL`, revoked on unmount.
  Limited to about 4 in flight (ponytail: a tiny queue; lazy-load with
  `IntersectionObserver` only if large libraries hurt).
- Playback URL: `/Videos/{id}/master.m3u8` with `mediaSourceId`, `PlaySessionId`
  (from PlaybackInfo), `videoCodec=h264`, `audioCodec=aac`, and
  `SubtitleMethod=Hls` for a chosen track. Jellyfin decides remux vs
  transcode (proven in the POC).
- Resume: hls.js `startPosition` from a fresh `GET /Items/{id}`. Save
  `PlaybackPositionTicks` every 10 s, on pause/seek, on unmount and on
  `pagehide`; on `ended`, set `Played` and reset the position.
- Routing: no router dependency. A `useState` view stack (gateway → view →
  folder → item → player); browser Back is out of scope (ponytail: add
  `history.pushState` if it annoys).

### TURN
`GET /turn` (Firebase Bearer) → `{iceServers}`. Prod: Cloudflare Realtime TURN
"generate ICE servers" with a ~1 h TTL (new secrets `CLOUDFLARE_TURN_KEY_ID`
and `CLOUDFLARE_TURN_API_TOKEN` → `docs/tools.md`). Dev: when they're unset,
return the compose coturn static credentials, or STUN only when coturn isn't
running. The gateway already uses the ICE servers it is configured with; it
answers, so the browser's relay candidates are enough (verify early).

### Telemetry + privacy indicator
One poller on `pc.getStats()` (every 2 s) feeds both:
- **Indicator**: selected candidate pair → local/remote `candidateType`
  (`relay` = relayed), bytes received.
- **Telemetry**: `POST /connections` on connect (`gatewayId`, relay, ipv6,
  candidate types) → `{id}`; `PATCH /connections/{id}` on end (bytes,
  duration). Table `connection_sessions` with `tenant_id`, behind the global
  filter. Migration #4. `Features/Poc/Telemetry.cs` and its table are deleted.

## Delivery: two PRs on `feat/streaming-client`

- **PR A: watch it.** Gateway credential injection + allowlist + redaction +
  request body; `tunnelFetch` + hls.js loader; library/series/episode browse;
  player with seek + subtitles; resume; POC deletion (telemetry sink included:
  real streams log nothing until PR B, tracked in TECH_DEBT.md).
- **PR B: trust it.** `/turn` + client TURN; `connection_sessions` (migration #4);
  privacy indicator.

## Testing

- Go: allowlist table test (allowed + refused, including path traversal
  `/Videos/../Startup`, encoded slashes); credential injection/stripping and
  `/Users/me` rewrite via the existing pure `proxy` core; body cap.
- TS (`node --test`): Range conversion (hls.js exclusive end → HTTP
  inclusive), ticks ↔ seconds.
- Backend: Reqnroll, where a tenant can't read or patch another tenant's
  `connection_sessions` row. `/turn` requires auth.
- **E2E golden path, extended**: global setup makes a 30 s fixture (+ `.srt`)
  inside the Jellyfin container with its bundled ffmpeg (no host ffmpeg in CI),
  then completes the startup wizard over the API (`e2e/jellyfin.ts`: user,
  library, scan, API key). Then sign in → pair → Open → Movies → clip →
  subtitle picker shown → `currentTime > 3` → Back → reload → Continue watching
  shows "Resume 0:0x" → playback starts past 2.5 s.
  PR B adds a relay-forced variant (coturn profile, `iceTransportPolicy: relay`)
  where the indicator says "relayed".

## Docs in the same diff

`docs/protocol.md` (tunnel framing → MVP, `body`, allowlist, POC section
deleted); CLAUDE.md current state; TECH_DEBT.md (delete "POC paths still
live" in A, "POC telemetry sink" in B); `docs/tools.md` (Cloudflare TURN
secrets, B); README/dev notes: create the Jellyfin API key and set
`STREAMIQ_JELLYFIN_API_KEY`.

## Open risks / verify early

- ~~Jellyfin 12.4 API paths~~: verified against the pinned image's OpenAPI and
  live calls; the table above is the result.
- ~~hls.js loader typing~~: written against hls.js 1.7 types.
- Jellyfin's first library scan can race the new library's options and index
  `/media` as a plain folder; the e2e helper re-triggers `/Library/Refresh`.
- Poster fan-out over one-channel-per-request: SCTP stream limits (Pion
  default 65535, Chrome caps lower). The in-flight cap covers it.
- ~~TURN with the browser as offerer and the gateway on STUN only~~: relay-only
  sessions connect (e2e forced-relay step). Chrome may report the relayed local
  candidate as `prflx`; `relayProtocol` gives it away.

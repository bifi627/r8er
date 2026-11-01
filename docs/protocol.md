# r8er signaling protocol

Contract-first (per CLAUDE.md). Below the onboarding section, this is the **POC seed** — just enough to
carry a WebRTC handshake between two peers through the signaling relay. Codegen
(C#/Go/TS from one schema) is deferred until the protocol stabilizes; for now
the envelope is hand-mirrored in Go (`agent/internal/poc`, `agent/internal/tunnel`) and JS
(`dev/throughput.html`, `dev/tunnel.html`).

## Agent onboarding (MVP item 2)

Design: `superpowers/specs/2026-11-01-r8er-agent-pairing-design.md`.
Fingerprints everywhere use the form `sha-256 AA:BB:…` (32 upper-case hex
pairs, SHA-256 over the DER certificate).

**`POST /agents/enrollment-tokens`** (Firebase Bearer) →
`{ token, expiresAt, installCommand, installCommandPowershell }`. Token is
random 128 bit, single-use, 15 min; server stores only its SHA-256.

**`POST /agents/enroll`** (no auth — the token is the auth)
```json
{ "token": "…", "fingerprint": "sha-256 AB:…", "name": "nas" }
```
→ `200 { "deviceId": "<uuid>", "credential": "…" }` (credential: 256 bit,
shown once, server keeps its SHA-256) · `400` malformed fingerprint ·
`401` token unknown, expired, or used.

**`GET /agents/ws`** — agent control WebSocket, `Authorization: Bearer
<credential>` (`401` before upgrade if unknown). Agent → server, one JSON text
frame on connect and every 30 s:
```json
{ "type": "heartbeat", "lanUrl": "http://192.168.1.20:8097" }
```
Server sets `last_seen_at = now` and `lan_url` (http/https only, else null).
`GET /devices` reports `online` = last heartbeat within 90 s. PR B adds
signaling messages on this socket.

## Transport

A peer opens a WebSocket to `/signaling/{room}`. The relay forwards every frame
verbatim to the *other* peer in the room (max two peers). It never inspects
payloads. Rendezvous is by shared `room` id, agreed out-of-band (hardcoded
`poc` for the POC).

## Message envelope

One JSON object per WebSocket text frame:

```json
{ "type": "offer" | "answer" | "ice", "sdp": <SessionDescription>, "candidate": <ICECandidateInit> }
```

| `type`   | carries      | direction        |
|----------|--------------|------------------|
| `offer`  | `sdp`        | browser → agent  |
| `answer` | `sdp`        | agent → browser  |
| `ice`    | `candidate`  | both (trickle)   |

- `sdp` is the WebRTC `RTCSessionDescription` (`{type, sdp}`), present only on `offer`/`answer`.
- `candidate` is `RTCIceCandidateInit` (`{candidate, sdpMid, sdpMLineIndex, ...}`), present only on `ice`. Candidates trickle as they are gathered; end-of-candidates (a null candidate) is not signaled — POC lets gathering finish naturally.

## POC roles

The **browser is the offerer** and creates the data channel; the **agent is the
answerer**. The agent must be in the room first (it is the long-lived peer). No
auth, no tenancy — MVP promotes both the relay and this envelope into the
tenant-isolated broker.

## Tunnel framing (POC step 4)

Once the PeerConnection is up, the browser tunnels HTTP over data channels.
**One data channel per request** — the channel *is* the request/response
lifecycle, so there are no request IDs, no length prefixes, and no multiplexing
(and no head-of-line blocking between requests). The agent dispatches inbound
channels by label: `throughput` → flood benchmark (step 3); anything else → a
tunnel request.

Sequence on a request channel:

1. **Browser → agent** (one text frame): the request.
   ```json
   { "method": "GET", "url": "/web/index.html", "headers": { "Range": "bytes=0-99" } }
   ```
   `url` is origin-relative; the agent prepends its configured origin
   (`R8ER_ORIGIN`, default local Jellyfin `http://localhost:8096`). Headers are
   forwarded verbatim — **`Range` must survive intact or seek breaks.**
2. **Agent → browser** (one text frame): the response head.
   ```json
   { "status": 206, "headers": { "Content-Range": "bytes 0-99/9723", "...": "..." } }
   ```
3. **Agent → browser** (zero or more binary frames): the body, in ~16KB chunks
   under `bufferedAmount` backpressure (never a whole segment in one message).
4. **Agent closes the channel** — close is the body's **EOF** marker.

Frames are told apart by type: the head is a **text** frame, body chunks are
**binary**. POC tunnels body-less GETs only (playlists, segments, ranged reads).

**The tunnel is byte-transparent.** The agent forwards request headers verbatim
and relays the origin's response body unchanged — it does **not** negotiate or
apply content compression on the browser's behalf (Go's default transport would
inject `Accept-Encoding: gzip` and silently decompress, rewriting the body and
dropping `Content-Length`). Compression stays end-to-end: if the browser sends
`Accept-Encoding: gzip`, it is forwarded, the origin gzips, and the gzipped
bytes + `Content-Encoding` reach the browser to decompress. Media segments are
already compressed, so this costs nothing that matters.

### Browser mechanism: hls.js custom loader, not a Service Worker (decided)

Both were on the table (feasibility doc; handover notes). **Pick: hls.js custom
loader**, for the POC and MVP browser client. Playback is Jellyfin **HLS**
anyway, so a custom fragment/playlist loader reads straight from the data
channel in **page context** — where the channel handle already lives. A Service
Worker would add a `postMessage` hop (the channel can't live in the SW), needs
register+activate before it controls the page, and can be killed mid-stream. A
SW only earns its keep to transparently proxy *arbitrary* Jellyfin API traffic
behind a real URL (e.g. iOS native-HLS, which needs a URL) — deferred until a
concrete need appears. The `tunnelFetch` primitive in `dev/tunnel.html` is the
page-context transport the loader will wrap in step 5.

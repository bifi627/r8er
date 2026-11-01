# r8er signaling protocol

Contract-first (per CLAUDE.md). MVP sections first (onboarding, broker,
pairing), then the Phase-0 POC seed that item 5 deletes. Codegen (C#/Go/TS
from one schema) is deferred until the protocol stabilizes; for now messages are
hand-mirrored in Go (`agent/internal/{onboarding,sessions,pairing,tunnel}`), C#
(`Features/Broker`) and TS (`frontend/src/features/pairing`).

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
`GET /devices` reports `online` = last heartbeat within 90 s. The same socket
carries the agent's half of the broker (below).

## Broker (MVP item 3)

Tenant-scoped signaling. One JSON object per WebSocket text frame. The broker
relays SDP/ICE; it can't make either side trust the other (pins + pairing do).

**`GET /broker/ws`** — browser socket, **one socket = one session to one agent**.
Closing it ends the session (broker sends the agent `bye`).

| direction | message |
|---|---|
| browser → broker | `{type:"auth", token:"<Firebase ID token>"}` — first frame (not a query param: keeps it out of logs). Bad token → close `1008`. |
| broker → browser | `{type:"ready"}` |
| browser → broker | `{type:"offer", deviceId, sdp}` — browser is the offerer |
| broker → browser | `{type:"error", error:"agent offline or unknown"}` — also for another tenant's device (no existence oracle) |
| browser → broker | `{type:"ice", candidate}` |
| broker → browser | `{type:"answer", sdp}` · `{type:"ice", candidate}` |

**Agent side** (on `/agents/ws`): broker → agent `{type:"offer", session, sdp}`,
`{type:"ice", session, candidate}`, `{type:"bye", session}`; agent → broker
`{type:"answer"|"ice", session, …}`. The broker only routes an agent's message
into a session opened *for that device*. The agent keeps one PeerConnection
per session (several browsers at once).

## Pairing + connection auth (MVP item 3)

Design: `superpowers/specs/2026-11-01-r8er-agent-pairing-design.md`. Browser
identity = non-extractable WebCrypto ECDSA P-256 **device key**; its
fingerprint = `sha-256 AA:…` over the SPKI bytes. The **agent pin** = the
agent's DTLS fingerprint (`a=fingerprint` of the answer SDP). The browser
checks the pin **before** `setRemoteDescription` and refuses on mismatch
(pairing again is allowed: the comparison code authenticates the new cert).

The browser opens a data channel labelled **`auth`** (JSON text frames). Until
it is authorized, the agent closes every other channel immediately.

| # | direction | message |
|---|---|---|
| 1 | agent → browser | `{type:"challenge", n}` — n = hex(32 random bytes) |
| 2 | browser → agent | `{type:"auth", spki, sig}` (base64). `sig` = ECDSA P-256/SHA-256, **raw r‖s (64 bytes, IEEE P1363)** over `utf8("r8er-auth-v1\|" + agentDtlsFp + "\|" + browserDtlsFp + "\|" + n)` — both DTLS fps of *this* session, so a MITM can't relay it |
| 3 | agent → browser | `{type:"ok"}` (key on the allowlist → tunnel channels allowed) or `{type:"unpaired"}` |
| 4 | browser → agent | `{type:"commit", h, name}` — h = hex(SHA-256(utf8(nB))), nB = hex(32 random bytes) |
| 5 | agent → browser | `{type:"nonce", n: nA}` — only after the commit |
| 6 | browser → agent | `{type:"reveal", n: nB}` — agent checks it against h |
| 7 | both | `code = uint32_be(SHA-256(utf8(agentFp + "\|" + keyFp + "\|" + nA + "\|" + nB))[0..4]) mod 1e6`, zero-padded to 6 digits; agent page lists the request (name + code), browser shows the code |
| 8 | agent → browser | `{type:"approved"}` after **Approve on the agent's local page** (key persisted to `allowlist.json`; browser stores the pin), or `{type:"rejected", reason}` (Reject, 5-min expiry, >3 pending, bad signature/reveal) |

The commit makes the code ungrindable: a MITM gets one guess (10⁻⁶).

**Shared test vectors** (asserted in `agent/internal/pairing` and
`frontend/src/features/pairing/crypto.test.ts`; generated with WebCrypto):

```
pkcs8     MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgNfSxgTmdmEdSnpZREJ8zENfSXNkksKt+maxxPfmM/0OhRANCAASZO24jDJAtjsPMwyNMixSTFrNaCBNOcl++jKq1UlDMtdhrPnx+TVcO+RDzLnEwFciaW/hHW06pJCe99d4wkcc3
spki      MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmTtuIwyQLY7DzMMjTIsUkxazWggTTnJfvoyqtVJQzLXYaz58fk1XDvkQ8y5xMBXImlv4R1tOqSQnvfXeMJHHNw==
keyFp     sha-256 40:F0:B4:7F:13:E4:C0:9B:73:86:7F:7A:77:E1:16:71:A2:AF:84:61:A0:8F:9F:15:25:CB:FE:7F:82:8A:C6:10
agentFp   sha-256 D4:F0:BC:5A:29:DE:06:B5:10:F9:AA:42:8F:1E:ED:BA:92:60:12:B5:91:FE:F7:A5:18:E7:76:A7:C9:BD:18:24
browserFp sha-256 D4:C3:E8:A1:12:56:AB:82:A4:FC:72:56:0E:B4:A2:B0:E8:7B:AD:82:0C:29:0D:D9:B0:36:16:DE:24:0A:A6:DB
n         00…01 (64 hex chars)
sig       x4k+pvKpTAYliFPG+JHOlJbXTRZ8F9qtAO+AH4FqD61nmYmrSo7RLbJ1AaQNWHkXj4kbaOVbwHZkdBK51PiBcg==   (verifies)
code(agentFp, keyFp, nA="11"×32, nB="22"×32) = 138781
```

Once authorized, tunnel requests use the framing below, one channel each.

# POC (Phase 0) — deleted by item 5

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

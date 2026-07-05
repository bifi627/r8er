# r8er — Feasibility Analysis

**Date:** 2026-07-05
**Status:** Feasibility / high-level design. Not an implementation plan.

## What r8er is

A multi-tenant media management platform with hybrid storage. Users manage a
media library whose files live in two places:

- **Sensitive files** stay on the user's own hardware (NAS / PC / phone) and
  never leave it.
- **Non-sensitive files** may be uploaded to r8er's cloud object storage.

Users stream their media from a browser: on the local network when home, over
the internet when away — **without configuring their router**. That last part
is the core selling point and the main thing that differentiates r8er from
Plex.

## Decisions locked in

These were resolved during brainstorming and constrain the architecture:

1. **"P2P" means device-to-device, not a swarm.** The bytes flow directly
   between the user's own devices (phone ↔ home host). No BitTorrent-style
   untrusted-peer model. This removes the hardest part of "P2P."
2. **Best-effort availability.** If a host is offline, its files are simply
   unavailable (greyed out). No wake-on-LAN, no forced always-on box, no
   inbound port requirements.
3. **Multi-tenant SaaS, but Bring Your Own Storage.** Strangers sign up. But
   r8er never holds customers' *sensitive* files — those stay on their
   hardware. r8er only holds metadata, coordination state, and *non-sensitive*
   files in its own cloud storage. No extra encryption beyond what the cloud
   provider gives. This offloads most data-liability risk.
4. **Browser-first client; native app later.** Browser-first forces WebRTC as
   the transport for reaching home hosts (browsers can't speak WireGuard/raw
   sockets to an arbitrary box). Native app comes later for better streaming
   control and better NAT traversal.

## Reference product

**Plex** is the closest shipping analogue and validates the overall shape:
shared cloud control plane (plex.tv) + user-run host agents (Plex Media
Server) + relay fallback + browser and native clients. The model is not
speculative — it has run at scale for 15 years.

Two deliberate departures from Plex:

- **Connectivity:** Plex reaches home servers via UPnP **port-forwarding**
  (inbound) + a `*.plex.direct` TLS trick + throttled relay. Its #1 support
  complaint is "Remote Access unavailable" because port-forwarding fails
  constantly (UPnP disabled, CGNAT). r8er uses **WebRTC hole-punching**
  (outbound-only), which needs no router config and succeeds on more networks.
- **Cloud:** Plex Cloud (a running server per user) was killed in 2018 —
  too expensive/operationally painful. r8er avoids the trap by using **object
  storage** for cloud files, never a server-per-tenant. **Do not drift toward
  hosting customer servers in our cloud.**

## Architecture

```
          ┌──────────────── r8er CLOUD (control plane) ─────────────────┐
          │  Accounts · tenants · auth · media catalog/metadata         │
          │  Device registry · file index (cloud vs host X)             │
          │  WebRTC signaling broker                                    │
          │  Sees metadata only. Never sees sensitive bytes.            │
          └───────────┬─────────────────────────────────┬───────────────┘
                      │ signaling (SDP/ICE)              │ signed URLs
                      │                                  │
   ┌──────────────────┴─────┐              ┌─────────────┴───────────────┐
   │  BROWSER CLIENT (phone)│              │  CLOUD OBJECT STORE (R2/B2) │
   │  media app UI          │              │  non-sensitive files only   │
   └──────────┬─────────────┘              └─────────────────────────────┘
              │  WebRTC (bytes go device-to-device, DTLS-encrypted)
              │  ICE picks best path: LAN direct → STUN direct → TURN relay
   ┌──────────┴─────────────┐
   │  HOST AGENT (NAS/PC)   │  serves sensitive local files
   │  on CUSTOMER hardware  │  online → streamable, offline → greyed out
   └────────────────────────┘
```

### Components

- **Control plane** (r8er's always-on backend): web app + API, accounts,
  tenants, auth, media catalog, device registry, file index, and the WebRTC
  **signaling broker** (a thin WebSocket matchmaker). Sees metadata only; file
  bytes never pass through it.
- **Cloud object store**: non-sensitive files, served via signed URLs + CDN.
- **Host agent**: runs on the customer's own hardware. Registers with the
  control plane, reports online/offline, indexes local files, and serves them
  over WebRTC to authorized clients of the same tenant. **This is the only
  genuinely novel component to build.**
- **Browser client**: media app. Cloud files → signed URL. Local files →
  if host online, establish WebRTC via signaling and stream; else greyed out.

## How connectivity works (the selling point)

"No router configuration" is achievable and always true, because every
connection is **outbound**, which NATs allow by default. Nobody accepts an
inbound connection (the thing port-forwarding requires).

1. Home agent and phone each ask a **STUN** server (outbound) "what's my public
   IP:port?"
2. They swap those addresses through the **signaling** server (each reached via
   an outbound WebSocket). Signaling is a note-passer; no file bytes.
3. Both fire packets at each other simultaneously; each outbound packet opens a
   return hole in its own NAT. The packets cross and punch through — **UDP hole
   punching**. Direct P2P stream follows, DTLS-encrypted.
4. On the home LAN, ICE finds the local candidate and connects directly on the
   LAN at full speed — the "home = LAN, away = internet" behavior is free from
   ICE, not a second code path.

**Honest caveat — symmetric NAT.** Some NATs (notably **carrier-grade NAT**,
common on mobile) assign a different port per destination, so hole-punching
fails. WebRTC then falls back to **TURN relay**: bytes flow through a relay
server. Still zero config for the user; the cost is relay bandwidth (see
below). Real-world direct-connect success is roughly **80–90%**; the rest
relay.

**No certificate workaround needed.** The Plex `*.plex.direct` cert machinery
exists only because Plex does plain HTTPS to a home IP. WebRTC secures the media
channel with **DTLS using self-signed certs**, verified by **fingerprint**
passed through signaling — not against the public CA chain. The only TLS r8er
runs is the ordinary cert on its own control-plane domain. Home boxes need no
public cert and no DNS trick.

## Hosting

The host agent runs on customer hardware, so r8er only operates the control
plane. Operational footprint: **one Railway project + a Cloudflare TURN account
+ an object-storage bucket.**

| Piece | Host | Notes |
|---|---|---|
| Web app / API | **Railway** | HTTP domain |
| Signaling (WebSocket broker) | **Railway** | WSS rides HTTP |
| Postgres (catalog, tenants, devices) | **Railway** | Managed |
| Redis (presence: who's online) | **Railway** | Managed |
| STUN/TURN relay | **Cloudflare Realtime TURN** | Railway has **no UDP ingress**; TURN needs UDP + relay port range + static IP |
| Cloud file storage | **Cloudflare R2 / Backblaze B2** | Object store + signed URLs; R2 has no egress fees |

TURN and object storage live off Railway not because Railway is deficient, but
because UDP-relay and blob storage need specialized infra on *any* PaaS. Both
attach to the design by a **config string** — no architectural coupling.

Railway networking confirmed (2026-07-05, Railway docs): HTTP domains + TCP
proxy (single raw TCP port). **No UDP ingress** — which is why TURN cannot run
on Railway.

## Cost model (TURN)

**Cloudflare Realtime TURN: $0.05/GB, billed only on outbound to the client;
ingress free** (verified 2026-07-05). The "free with Realtime SFU" clause is
for multi-party conferencing, not r8er's 1:1 streaming, so the $0.05/GB applies.

Only relayed streams touch TURN; direct-connected streams cost $0. So:

```
TURN cost ≈ (GB streamed) × (relay fallback %) × $0.05/GB
```

Worked example — 1080p ≈ 5 Mbps ≈ 2.25 GB/hr; a heavy user at 100 hrs/month =
~225 GB:

| Relay rate | Relayed GB | Cost/user/month |
|---|---|---|
| 10% | 22.5 | $1.13 |
| 20% | 45 | $2.25 |
| 100% (worst case) | 225 | $11.25 |

Cheap for casual users, low single digits for binge-watchers — as long as the
direct-connect rate holds. Among managed TURN providers this is at the cheap end
(Twilio ≈ 8×). Relayed traffic is DTLS ciphertext, so it preserves the "r8er
never sees sensitive bytes" property.

**Watch:** TURN pricing intuition is built for calls (~1 Mbps); video is
heavier (4K ≈ 15–25 Mbps ≈ 7–11 GB/hr). The relay % caps exposure; **adaptive
bitrate** in the host agent (transcode down for remote/relayed sessions) is the
later mitigation.

## Feasibility verdict

**Feasible. No exotic technology.** The overall shape is proven by Plex. The
only genuinely novel engineering is the **host agent** (and specifically
video seek over the WebRTC transport). Everything else is standard SaaS +
managed TURN + object storage.

## Risks, in order

1. **TURN relay bandwidth = opex.** Bounded by the relay-fallback rate, which
   is the single most important number to measure early. Video volume makes it
   matter more than typical WebRTC.
2. **Host agent packaging & support.** Shipping software to Synology / unRAID /
   Windows / Mac / Linux / phones is the real support surface. Start with Docker
   + one platform; expand on demand.
3. **Video seek over WebRTC.** Range-request/seek on a custom transport is the
   core engineering effort and the true feasibility gate. (Plex gets seek free
   via HTTP range requests; r8er trades that away for no-config connectivity.)

## Build order (de-risks by shipping value early)

- **Phase 0 — Cloud-only.** Catalog + multi-tenant auth + non-sensitive files
  from object storage. Boring, shippable, no NAT. Proves the product.
- **Phase 1 — Host agent + WebRTC.** The novel part. **Prototype video
  seek-over-WebRTC first**, and run a **connectivity spike**: connect from many
  real networks and log direct-vs-relay rate to turn the cost model into a
  measurement.
- **Phase 2 — Native app.** Better streaming, better NAT traversal, lower
  relay bill, adaptive bitrate.

## Open questions (not yet examined)

- **Multi-tenant signaling isolation:** how the signaling broker guarantees
  tenant A's phone can never punch into tenant B's host. (Identified as the last
  unexamined design piece — belongs in the Phase-1 design.)
- Host-agent authentication / device pairing flow.
- Recommended off-the-shelf libraries (browser `RTCPeerConnection`; **Pion**
  (Go) for the headless host-agent peer) — to be confirmed at implementation
  time.

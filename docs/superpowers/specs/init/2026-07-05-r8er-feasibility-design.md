# r8er — Feasibility Analysis & Initial Design

**Date:** 2026-07-05
**Status:** Initial design document. Feasibility + high-level design. Not an implementation plan.
**Harmonized 2026-07-05** with `2026-07-05-r8er-implementation-plan.md`, which
owns phasing and stack choices (Jellyfin bundled, Go/Pion agent, Firebase
auth, cloud files deferred to QOL). This document owns architecture
rationale, risks, privacy rules, and the cost model.

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
   provider gives. This offloads most data-liability risk — **but see
   "Content liability" below: the cloud-file half of the product carries real
   hosting obligations that must be scoped deliberately.**
4. **Browser-first client; native app later.** Browser-first forces WebRTC as
   the transport for reaching home hosts (browsers can't speak WireGuard/raw
   sockets to an arbitrary box). Native app comes later for better streaming
   control and better NAT traversal. **Caveat:** on iOS Safari, Media Source
   Extensions support is limited (Managed MSE only, with constraints) and
   background/lock-screen playback in a web app is poor. "Browser-first" in
   practice means **Android-and-desktop-first**; iPhone users get a degraded
   experience until the native app. This is accepted, and it raises the
   priority of the native phase.
5. **The user classifies files as sensitive or non-sensitive**, per file or
   per folder, at import time. **Default: sensitive** (stays local). A wrong
   default here is a trust incident and a legal exposure; opting *into* cloud
   storage must be explicit.
6. **Stack and engine (locked during planning):** the host agent is **Go +
   Pion** and bundles a **Jellyfin** instance as its internal media engine
   (users only see r8er's UI); the control plane is **.NET 10** with a
   **React** client; auth is **Firebase**; **cloud file storage is deferred
   to the QOL phase** — Firebase is auth-only until then. Rationale and
   fallbacks in the implementation plan.

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

One lesson to take *from* Plex rather than against it: **Plex Media Server is,
above all, a transcoder.** The library UI and remote access are the visible
features; the reason it is a heavy install with hardware requirements is that
real-world media files mostly cannot be played by browsers as-is. r8er
inherits this problem in full — see "Media playback & transcoding."

## Alternatives considered

### Cloudflare Tunnel instead of WebRTC — rejected

`cloudflared` on the host would give a public HTTPS URL with zero router
config — the selling point with no WebRTC at all, plain HTTP range requests,
seek for free. Rejected because **Cloudflare terminates TLS and sees the
bytes**, which breaks the core promise that sensitive files never leave the
user's control, and because proxying bulk video through Cloudflare's tunnel
has ToS and cost uncertainty at scale. WebRTC's DTLS is end-to-end between
the user's own devices; no intermediary sees plaintext (a TURN relay only
ever sees ciphertext).

### The Jellyfin wedge — evaluated; resolved in favor of bundling

Jellyfin (open-source Plex) already solves everything hard about media
serving — library management, transcoding, seek, subtitles, metadata
scraping, client apps — but has **no remote-access story**: its users must
port-forward or run a VPN/Tailscale, and they feel this pain acutely today.

A much smaller product exists inside r8er: a host agent that is essentially a
**WebRTC tunnel in front of a local Jellyfin instance**, plus the control
plane for signaling and auth. That ships the differentiator (no-config remote
access) with a fraction of the build surface, to an existing community, and
produces the same real-world data (relay rates, packaging costs) the full
product needs. The full r8er platform can grow out of it later.

**Decision (2026-07-05):** r8er ships the full product with **Jellyfin
bundled inside the host agent** as its media engine — not a wedge product
for existing Jellyfin users. Jellyfin still solves the hard parts
(transcoding, seek, library, subtitles), but behind r8er's own UI. The wedge
remains the documented **pivot** if the POC checkpoint kills the bundled
approach or the relay economics fail.

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
  **Deferred to the QOL phase** — no cloud file storage until then.
- **Host agent** (Go/Pion): runs on the customer's own hardware. Registers
  with the control plane, reports online/offline, and serves media to
  authorized clients of the same tenant. Internally it is a **bundled
  Jellyfin instance fronted by an HTTP-over-WebRTC-data-channel tunnel** (see
  below) — not a custom media protocol or a homegrown media server. **This is
  the only genuinely novel component to build, and the novel part is the
  tunnel + packaging, not a streaming protocol.**
- **Browser client**: media app. Cloud files → signed URL. Local files →
  if host online, establish WebRTC via signaling and stream through the
  tunnel; else greyed out.

### Host agent transport: HTTP over the data channel, not a custom protocol

Earlier drafts framed "video seek over WebRTC" as the core novel engineering.
It is not, if the transport is shaped correctly. Two standard shapes, both
acceptable; pick at prototype time:

1. **HTTP-over-data-channel tunnel (default choice).** The agent runs an
   ordinary local HTTP endpoint (serving raw files and transcoded streams);
   the WebRTC data channel tunnels HTTP requests/responses to it. Range
   requests — and therefore seek — work exactly as they do for Plex. The
   tunnel is generic plumbing, media-agnostic, and reusable (it is also what
   the Jellyfin-wedge alternative would use).
2. **Segmented streaming (HLS-style).** The agent serves small segments over
   the data channel; seek = fetch a different segment; the client feeds MSE.
   Composes naturally with transcoding; this is how every streaming service
   does seek.

Browser side, two candidate mechanisms: a **Service Worker** intercepting
fetches to a virtual origin, or an **hls.js custom loader** reading segments
from the data channel in page context (no Service Worker needed). The POC
evaluates both; pick the dumber one that works.

Either way, "seek" drops off the risk list as a research problem and becomes
plumbing. **Known constraint to verify in the spike:** browser SCTP data
channels have historically capped at a few tens of Mbps depending on
implementation — fine for 1080p (~5 Mbps), needs measurement for 4K.

## Media playback & transcoding — feasibility gate #1

Browsers cannot play most of what real media libraries contain: MKV
containers, HEVC/AV1 on many devices, AC3/DTS/TrueHD audio, embedded
subtitles. Connectivity that works perfectly will still end in a black screen
for a large fraction of files unless the host agent remuxes or transcodes.
This — not NAT traversal — is the Plex-hard part of the product.

Consequences:

- The media engine must **remux** (rewrap compatible codecs into fMP4/HLS)
  and **transcode** (video/audio conversion, subtitle burn-in or extraction).
  **Bundling Jellyfin satisfies this without building it** — Jellyfin ships
  ffmpeg and a mature transcode pipeline; r8er does not write one.
- **Hardware matters.** A weak NAS cannot software-transcode 4K. Hardware
  acceleration (Quick Sync via `/dev/dri`, VAAPI, NVENC) is a packaging and
  support problem per platform — this significantly weights the "host agent
  packaging" risk even with Jellyfin doing the work.
- **Adaptive bitrate is transcoding.** The cost-model mitigation of
  "transcode down for relayed sessions" is not a later add-on; it is the same
  transcode pipeline (Jellyfin quality settings), configured per session.
- **Fallback stance (if Jellyfin fails its POC checkpoint):** the Go agent
  serves media itself, **remux-only**, with an explicit, documented
  compatibility constraint (roughly: H.264/AAC in any common container plays;
  HEVC/DTS/etc. is flagged "not yet streamable" in the UI, not greyed out
  silently). Full transcoding becomes the fast-follow, prioritized by real
  library telemetry.

**Spike required (with the connectivity spike):** run a codec/container
census over a handful of real user libraries — validates Jellyfin's transcode
coverage and sizes how much the remux-only fallback would actually cover.

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

**Honest caveat — symmetric NAT, and it hits the flagship case hardest.**
Some NATs (notably **carrier-grade NAT**, standard on mobile data) assign a
different port per destination, so hole-punching fails. WebRTC then falls
back to **TURN relay**. Still zero config for the user; the cost is relay
bandwidth (see below). General-population WebRTC direct-connect rates of
**80–90%** are often quoted, but that population includes desktop-to-desktop;
**r8er's flagship scenario — a phone on cellular punching to a home NAT — is
close to the worst case** and its direct rate is unknown. Treat the relay
rate for *cellular-to-home specifically* as an unmeasured number, not 10–20%.

**IPv6 is the biggest free lever on this number.** Most mobile carriers are
IPv6-native; if the home connection also has IPv6, ICE gets a direct route
even through IPv4 CGNAT, no hole-punching required. Supporting it costs
nothing (ICE handles IPv6 candidates automatically); the spike must measure
direct rates with and without IPv6, and user docs should recommend enabling
IPv6 on the home router where available.

**No certificate workaround needed.** The Plex `*.plex.direct` cert machinery
exists only because Plex does plain HTTPS to a home IP. WebRTC secures the media
channel with **DTLS using self-signed certs**, verified by **fingerprint**
passed through signaling — not against the public CA chain. The only TLS r8er
runs is the ordinary cert on its own control-plane domain. Home boxes need no
public cert and no DNS trick. (Corollary: a fingerprint exchanged only
through signaling would make stream privacy only as strong as signaling
integrity — a compromised control plane could man-in-the-middle a session.
**Closed by fingerprint pinning at pairing time** — see "Provable privacy"
below.)

## Hosting

The host agent runs on customer hardware, so r8er only operates the control
plane. Operational footprint: **one Railway project + a Cloudflare TURN
account** (an object-storage bucket joins in the QOL phase when cloud files
ship).

| Piece | Host | Notes |
|---|---|---|
| Web app / API (.NET 10) | **Railway** | HTTP domain; also hosts signaling |
| Signaling (WebSocket broker) | **Railway** | WSS rides HTTP; same app as the API |
| Postgres (catalog, tenants, devices) | **Railway** | Managed |
| Auth | **Firebase Auth** | Google + email sign-in; .NET verifies ID tokens |
| Presence (who's online) | **Postgres heartbeat column** | ponytail: add Redis only if polling load demands it |
| STUN/TURN relay | **Cloudflare Realtime TURN** | Railway has **no UDP ingress**; TURN needs UDP + relay port range + static IP |
| Cloud file storage | **Deferred to QOL** | Provider re-decided then; prior favors R2 (no egress fees) over Firebase/GCS (~$0.12/GB egress) |

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

**The relay rate is an assumption, not a measurement** — and per the
connectivity section, the cellular-to-home rate may be materially worse than
the 10–20% rows. The connectivity spike exists to replace this table with
data. IPv6 adoption is the main lever that pulls the real number down.

Cheap for casual users, low single digits for binge-watchers — *if* the
direct-connect rate holds. Among managed TURN providers this is at the cheap
end (Twilio ≈ 8×). Relayed traffic is DTLS ciphertext, so it preserves the
"r8er never sees sensitive bytes" property.

**Watch:** TURN pricing intuition is built for calls (~1 Mbps); video is
heavier (4K ≈ 15–25 Mbps ≈ 7–11 GB/hr). The relay % caps exposure; **adaptive
bitrate** (transcode down for relayed sessions — same ffmpeg pipeline as the
playback gate above) is the mitigation.

## Privacy: metadata and derived data

"Sensitive files never leave the hardware" must extend to *derived* data, or
the promise is hollow: a thumbnail **is** content, and a filename often is
too.

Rule: **derived data of sensitive files (thumbnails, artwork, preview clips,
extracted subtitles) is generated on the host agent and stays on the host
agent.** The cloud catalog stores, for sensitive files, only what the user's
own client needs to render a library view: user-editable title, duration,
codec info, and an opaque file ID. When the host is offline, its entries
render with placeholder art — consistent with "greyed out."

What the control plane *does* see and store (and the docs must say so
plainly): titles/filenames as entered, library structure, watch state, device
metadata. Users for whom titles themselves are sensitive can rename entries;
r8er does not promise metadata-blindness, only byte-blindness.

### Provable privacy: fingerprint pinning + a visible indicator

**Decision (2026-07-05, added to MVP):** upgrade the privacy story from
promised to *provable*.

**Mechanism.** Device pairing happens on the LAN and carries the agent's
DTLS certificate fingerprint **out-of-band** (QR code shown by the agent,
scanned by the client; manual code fallback). The client **pins** that
fingerprint. Every subsequent WebRTC handshake verifies the remote
fingerprint against the pinned value — never against what the signaling
server relayed. Result: a compromised (or subpoenaed, or malicious) control
plane can *deny service* but can never *intercept a stream*. The claim
changes from "we won't look" to "we can't look."

**User-visible half.** A per-session privacy indicator in the client, backed
by real ICE stats: "end-to-end encrypted · direct connection · r8er servers
carried 0 bytes," or on relay "…relayed (encrypted, r8er cannot decrypt) ·
N MB relayed." Every competitor's connection status says *whether* you're
connected; this says *who could theoretically see your media*, with receipts.

**Why this feature, in this product (recorded so the reasoning survives):**

1. **It closes a documented hole.** The threat-model corollary above existed
   precisely because signaling-relayed fingerprints trust the relay. Pinning
   turns that caveat into a headline property.
2. **It is almost free given what is already planned.** The pairing flow
   exists (MVP step 2 — the claim code becomes a QR with a fingerprint in
   it); the per-session ICE stats already exist as the telemetry
   deliverable; the pin is a column and a comparison. Roughly a week,
   entirely on planned plumbing.
3. **It amplifies the core differentiator instead of adding sideways
   scope.** The product thesis is "sensitive files never leave your
   hardware"; this is the feature that makes the thesis *checkable* — a
   skeptic can read the client code and confirm the fingerprint never trusts
   the server.
4. **It is structurally hard to copy.** Competitors whose discovery/auth
   paths terminate TLS in their own infrastructure cannot honestly ship it;
   copying the indicator without the pinning is verifiably false. Moats made
   of architecture beat moats made of features.
5. **It has a bleeding-edge growth path (post-QOL, not planned):** publish
   agent fingerprints to a Certificate-Transparency-style log so audits can
   prove r8er never substituted a key for anyone.

**Rejected sibling idea, for the record:** Media-over-QUIC (MoQ) as the
transport — the actual bleeding edge of streaming delivery, but browsers
cannot do QUIC peer-to-peer to a home box, so it is architecturally dead on
arrival here; WebRTC remains the right call.

**Implementation warning:** pinning must be designed into the pairing flow
from the start (MVP step 2). Retrofitting pins onto already-paired devices —
key rotation, re-pairing UX, trust-on-first-use migration — is the painful
path.

## Content liability (cloud files)

*(Cloud file storage is deferred to the QOL phase; these obligations attach
the moment the upload feature ships — build them with it, not after.)*

The BYOS design offloads liability for sensitive files, but the **cloud half
hosts user-uploaded media bytes**, which carries the standard obligations of
any file host: DMCA takedown handling, abuse/CSAM response, and
payment-processor acceptability. This is not a reason to drop the feature; it
is a reason to scope it deliberately:

- **Cloud files are private-only in v1.** No public links, no cross-tenant
  sharing of cloud files. This removes the "distribution platform" exposure
  and most of the abuse surface.
- A designated DMCA agent and a documented takedown process exist **before**
  the first upload feature ships (this is cheap: registration + an email
  alias + a written procedure).
- Per-tenant upload quotas from day one (also caps the storage bill).
- Storage costs are passed through in pricing (below), so cloud storage never
  becomes an unbounded free perk.

## Sharing model and pricing (initial stance)

**Sharing:** a **tenant is a household**. One tenant has multiple member
accounts (family), all with access to the tenant's libraries per simple
per-library permissions. **Cross-tenant sharing (Plex-style friend shares) is
explicitly out of scope for v1** — it multiplies the auth, signaling-
isolation, and liability surface. Revisit after the QOL phase. This decision shapes
the auth model: authorization is "is this device/user a member of this
tenant," nothing finer-grained yet.

**Pricing (working hypothesis, to validate — needed because the cost model
above is meaningless without a revenue side):** freemium.

- **Free:** catalog + host-agent streaming with direct connections only (no
  TURN), small cloud quota. Costs r8er ~nothing per user.
- **Paid (~$5–8/mo, Plex-Pass-adjacent):** TURN relay fallback, meaningful
  cloud storage quota, multiple members.

The key property: **the expensive resources (relay GB, cloud GB) sit behind
the paid tier**, so worst-case variable cost (~$11/user/mo at 100% relay for
a heavy user) is bounded against revenue, and free users cannot run up the
TURN bill. Exact numbers are a business decision, not resolved here.

## Feasibility verdict

**Feasible. No exotic technology.** The overall shape is proven by Plex. The
genuinely novel engineering is the **host agent** — specifically the
HTTP-over-data-channel tunnel and the transcoding/packaging burden. The two
feasibility gates are:

1. **Playback compatibility** — whether the bundled Jellyfin can be driven
   cleanly through the tunnel (and, for the fallback, what fraction of real
   libraries remux-only covers), and whether transcode/hardware-accel
   packaging is tractable on target hardware.
2. **Cellular-to-home direct-connect rate** — the number that decides the
   TURN economics.

Both gates are cheap to test (spikes, below) and neither requires building
the product first. NAT traversal itself — previously overweighted — is
largely given by WebRTC/ICE.

## Risks, in order

1. **Playback compatibility & transcoding packaging.** Real libraries are
   full of files browsers can't play; the engine must remux/transcode, and
   hardware-accelerated transcoding across Synology/unRAID/Windows/Mac/Linux
   is the heaviest packaging and support surface. Mitigation: bundled
   Jellyfin does the transcoding; remux-only fallback with explicit
   compatibility messaging if Jellyfin fails its checkpoint; codec census
   spike.
2. **TURN relay bandwidth = opex, and the flagship path is the worst case.**
   Bounded by the relay-fallback rate — the single most important number to
   measure early, *specifically for cellular-to-home*. IPv6 is the main
   lever. Pricing gates relay behind the paid tier to cap exposure.
3. **Host agent packaging & support.** Shipping software to Synology / unRAID /
   Windows / Mac / Linux is the real support surface — now including ffmpeg
   and hardware-acceleration drivers. Start with Docker + one platform;
   expand on demand. **The agent must auto-update from day one**: a fleet of
   stale agents on customer NASes is a security and support problem that
   cannot be retrofitted easily.
4. **iOS browser experience.** Limited MSE and poor background playback make
   the browser client second-class on iPhone; raises native-app priority.
5. **Content liability on the cloud half.** Scoped down by private-only
   files, quotas, and a takedown process (above).

## Build order

**Superseded in detail by `2026-07-05-r8er-implementation-plan.md`** (POC →
MVP → QOL, with checkpoints). Mapping from this doc's earlier draft: the
spikes became the **POC**; host agent + WebRTC streaming became the **MVP**;
the cloud-only product moved to **QOL** (cloud file storage was deprioritized
— the home-media streaming differentiator ships first); native app sits in
QOL, promoted if POC shows iOS browser is unusable.

The gate logic survives unchanged — no product code before the spikes pass:

1. **Connectivity spike:** minimal WebRTC pair (browser ↔ headless agent);
   connect from many real networks with emphasis on **cellular-to-home**;
   log direct-vs-relay rates, IPv4 vs IPv6, and data-channel throughput.
   Replaces the cost-model assumption with a measurement.
2. **Seek/tunnel prototype:** HTTP range requests over the data channel
   into a `<video>` element; verify seek and sustained 1080p throughput.
3. **Codec census:** script over a few real libraries; validates Jellyfin
   coverage and sizes the remux-only fallback.

A week or two of work; the results decide whether the MVP proceeds as
designed, pivots to the Jellyfin wedge, or reprices.

## Open questions (not yet examined)

- **Multi-tenant signaling isolation:** how the signaling broker guarantees
  tenant A's phone can never punch into tenant B's host. (Designed in MVP
  step 3 of the implementation plan; security-reviewed at its Checkpoint 3.)
- Host-agent authentication / device pairing flow (MVP step 2).
- Tunnel shape final call (Service Worker vs hls.js custom loader; raw HTTP
  tunnel vs HLS segments) — decided by the POC seek prototype.
- Exact pricing tiers and quotas (business decision; constraints documented
  above).

Resolved since first draft: Jellyfin wedge (→ bundled engine, wedge kept as
pivot), agent stack (→ Go/Pion), auth provider (→ Firebase), cloud storage
phasing (→ deferred to QOL).

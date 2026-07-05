# r8er — High-Level Implementation Plan

**Date:** 2026-07-05
**Status:** Extended design / phased plan. High-level, sequential, with checkpoints.
Not a task-by-task implementation plan — each phase gets its own detailed plan
when it starts.
**Basis:** `2026-07-05-r8er-feasibility-design.md`, harmonized with this plan
on 2026-07-05 (Jellyfin bundled, Go/Pion agent, Firebase auth, cloud files
deferred to QOL). Should any stray disagreement remain, **this document wins
on phasing and stack**; the feasibility doc owns architecture rationale,
risks, privacy rules, and the cost model.

## Locked decisions (this document)

| Decision | Choice | Rationale |
|---|---|---|
| Backend | **.NET 10** (single app: API + signaling WebSocket) | Chosen stack; signaling is a thin WS handler, no separate service needed |
| Frontend | **React** (SPA, browser-first) | Chosen stack |
| Database | **Postgres on Railway** | Chosen stack |
| Hosting | **Railway** (API, signaling, Postgres) | Chosen stack; no UDP ingress → TURN stays external |
| Auth | **Firebase Auth** (Google + email sign-in) | Offloads identity; .NET verifies Firebase ID tokens |
| Cloud file storage | **Deferred entirely to QOL phase** | Main use case is serving home media; Firebase does auth only for now |
| Media engine | **Jellyfin, bundled** — the host agent installs/manages a Jellyfin instance as its internal engine; users only see r8er's UI | Jellyfin solves transcoding, seek, library scanning, subtitles — the Plex-hard parts |
| Host agent | **Go + Pion** | Most mature headless WebRTC data-channel stack; single static binary suits NAS/Docker. Accepted cost: second language |
| TURN | **Cloudflare Realtime TURN** | Per feasibility doc |
| Presence | **Postgres heartbeat column** for MVP | ponytail: a `last_seen_at` timestamp covers it; add Redis when polling load demands |

## Architecture in one paragraph

The **control plane** (.NET 10 on Railway) owns accounts, tenants, devices,
pairing, and WebRTC signaling. The **host agent** (Go/Pion, shipped as Docker
Compose: agent container + Jellyfin container) runs on the user's hardware,
keeps an outbound WebSocket to the control plane, and exposes its local
Jellyfin over an **HTTP-over-WebRTC-data-channel tunnel**. The **React
client** authenticates via Firebase, asks the control plane to broker a WebRTC
session to the user's agent, and then talks to Jellyfin *through the tunnel*
— library browsing and HLS video segments are ordinary HTTP requests carried
over the data channel, satisfied browser-side by a **Service Worker** or an
**hls.js custom loader** (POC step 4 evaluates both; pick the dumber one
that works). No inbound ports, no router
config; TURN relays when hole-punching fails.

## The Jellyfin fallback (decided up front, not improvised later)

Jellyfin is a candidate, not a certainty. Risks: API instability across
versions, bundling friction (GPLv2 — fine as a separate process communicating
over HTTP, but verify distribution details in POC), resource weight on small
NAS boxes.

**Fallback:** the Go agent serves media *itself* — ffmpeg remux to fMP4/HLS
(remux-only, per the feasibility doc's MVP stance), file indexing via a simple
directory scan, behind **the same tunnel**. The tunnel, signaling, pairing,
auth, and client are all engine-agnostic by construction: the client talks to
"the agent's local HTTP endpoint," whether that's Jellyfin or our own server.
This is why the tunnel is built and tested *before* deep Jellyfin
integration — it is the reusable asset either way.

**Fallback triggers (evaluated at Checkpoint 1):** Jellyfin's HTTP API can't
be driven cleanly through the tunnel; bundling/licensing blocker;
Jellyfin idle footprint unacceptable on target hardware.

---

# Phase 0 — POC

**Goal:** prove the two feasibility gates end-to-end with throwaway-quality
code: *a phone browser on cellular data streams and seeks a video from a home
Jellyfin instance, through the WebRTC tunnel, with zero router config.*

No auth, no multi-tenancy, no UI polish. Hardcoded single user.

**Steps, in order:**

1. **Baseline:** Jellyfin in Docker on the home box; confirm playback + seek
   on LAN with Jellyfin's own web client. (Establishes "the engine works"
   before adding our transport.)
2. **Signaling stub:** minimal .NET WebSocket endpoint that passes SDP/ICE
   between exactly two peers. Deploy to Railway early — signaling must work
   over the real internet, not localhost.
3. **Agent skeleton (Go/Pion):** connects outbound to signaling, answers a
   browser offer, opens a data channel. Measure raw data-channel throughput
   browser↔agent first — this bounds everything downstream.
4. **The tunnel:** HTTP-over-data-channel proxy in the agent (request in,
   local HTTP call to Jellyfin, response streamed back in chunks). Browser
   side: evaluate **both** a Service Worker (virtual origin, transparent
   proxying) and an **hls.js custom loader** (page context, no Service
   Worker) — pick the dumber one that works. Range requests must pass
   through intact either way.
5. **Playback through the tunnel:** React page with a `<video>` element
   playing Jellyfin's HLS stream via the tunnel. Verify: start, seek,
   sustained 1080p, subtitle track.
6. **Connectivity spike:** add Cloudflare TURN credentials; connect from
   cellular (the flagship worst case), friends' networks, café Wi-Fi. Log
   per-session: direct vs relay, IPv4 vs IPv6, negotiated path, throughput.
7. **Codec census:** script over 2–3 real libraries to confirm Jellyfin's
   transcode coverage handles them (informs the fallback's remux-only scope
   too).

### ✅ Checkpoint 1 — POC go/no-go (the big one)

Exit criteria — all measured, not assumed:

- [ ] End-to-end stream from cellular phone → home Jellyfin: plays, seeks, sustains 1080p.
- [ ] Direct-connect rate for cellular-to-home measured across ≥10 real network pairs; TURN fallback works; cost model updated with the real number.
- [ ] Data-channel throughput number recorded (and 4K verdict: feasible / needs native app).
- [ ] Service Worker tunnel approach confirmed on target browsers (Android Chrome, desktop; iOS Safari status documented honestly).
- [ ] Jellyfin verdict: bundle it (API drivable, licensing clear, footprint OK) **or trigger fallback** (agent serves media itself, remux-only).
- [ ] Decision recorded: proceed to MVP as designed / proceed with fallback engine / relay rate forces repricing or pivot (e.g., Jellyfin-wedge from the feasibility doc).

**Everything after this checkpoint assumes the POC numbers are good. Do not
start MVP before this gate.**

---

# Phase 1 — MVP

**Goal:** a stranger can sign up, install the agent, pair it, and stream
their home media remotely. Single member per tenant, one agent per tenant,
browser client, TURN fallback on.

**Scope philosophy (per project direction):** control-plane management and
media management are simple CRUD — keep them boring and let them evolve.
All engineering attention goes to the streaming path.

**Build order within MVP:**

1. **Control plane foundation (.NET 10 + Postgres + Firebase).**
   Firebase ID-token verification middleware; tenants (tenant = household,
   one owner account for now); device registry. Plain EF Core CRUD, no
   cleverness. React app: Firebase sign-in, empty shell.
2. **Agent pairing — with fingerprint pinning from day one.** Claim-code
   flow: agent prints a short code on first run → user enters it in the web
   app → agent receives a long-lived agent credential bound to the tenant.
   Pairing happens on the LAN and carries the agent's **DTLS certificate
   fingerprint out-of-band**: the agent shows a QR code (claim code +
   fingerprint), the client scans it and **pins** the fingerprint. Every
   later WebRTC handshake verifies the remote fingerprint against the
   *pinned* value — never against what signaling relayed — making the
   control plane cryptographically incapable of MITM (rationale: feasibility
   doc, "Provable privacy"). Fallback for QR-less setups: short code +
   fingerprint displayed for manual confirmation. **Pinning must be designed
   into the pairing flow now — retrofitting pins onto already-paired devices
   is the painful path.** Agent heartbeats over its WebSocket;
   `last_seen_at` in Postgres drives online/offline in the UI.
3. **Signaling with tenant isolation.** Promote the POC stub into the real
   broker: a client may only be brokered to agents of *its own tenant* —
   enforced server-side on every message, tested explicitly. (This was the
   feasibility doc's top open question; it gets designed and reviewed here.)
4. **Productized agent.** Docker Compose distribution (agent + Jellyfin),
   single-command install, **auto-update from day one** (agent
   self-updates; Jellyfin image pinned and updated by the agent), structured
   logs, a local status page.
5. **Streaming client.** React media UI: browse library (Jellyfin API
   through the tunnel — no cloud catalog yet; host offline simply shows
   "host offline," greyed-out cached browsing is QOL), play, seek, resume.
   TURN enabled. Includes the **live privacy indicator**: per-session, backed
   by real ICE stats — "end-to-end encrypted · direct connection · r8er
   servers carried 0 bytes," or on relay "…relayed (encrypted, r8er cannot
   decrypt) · N MB relayed." Pure UI over the connection telemetry already
   collected as a deliverable.
6. **Hardening pass before strangers arrive:** rate limits on signaling,
   TURN credential scoping (short-lived, per-session), agent credential
   revocation, backup/restore story for Postgres.

### ✅ Checkpoint 2 — internal dogfood

- [ ] Developer + 1–2 friendly users stream daily from real away-networks for two weeks.
- [ ] Pairing, auto-update, and reconnect-after-agent-restart all exercised.
- [ ] Relay-rate and throughput telemetry flowing into Postgres from real sessions.

### ✅ Checkpoint 3 — MVP release gate

- [ ] Security review of tenant isolation (signaling + tunnel auth) — written up, issues fixed.
- [ ] A non-developer completes install + pairing + first stream unassisted, using only the docs.
- [ ] Auto-update proven by shipping at least one real agent update to dogfood installs.
- [ ] Cost telemetry: TURN $/user/month for dogfood cohort computed; still within the pricing hypothesis.
- [ ] Open limited beta signup.

---

# Phase 2 — QOL

Ordered roughly by value; each item is independent and can be re-prioritized
from beta feedback. Detailed plans per item when picked up.

1. **Cloud catalog sync.** Agent pushes a metadata-only catalog (titles,
   durations, opaque IDs — honoring the feasibility doc's derived-data
   privacy rule: no thumbnails of sensitive files in the cloud) so the
   library browses instantly and offline hosts render greyed-out instead of
   invisible.
2. **Multi-member tenants.** Additional Firebase accounts per household,
   per-library permissions. (Still no cross-tenant sharing.)
3. **Cloud (non-sensitive) file storage.** The deferred feature: upload +
   stream from object storage. Re-decide the provider here (Firebase/GCS
   convenience vs R2 zero-egress) with real usage data; ship with
   private-only files, quotas, and the DMCA/takedown process from the
   feasibility doc.
4. **Relay-aware quality.** Detect relayed sessions and request lower-bitrate
   Jellyfin transcodes to cap TURN spend; user-visible quality selector.
5. **Agent platform expansion.** Synology package, Windows installer, etc. —
   driven by actual user demand, not speculation.
6. **Billing.** Freemium gates per the feasibility doc (TURN + quotas behind
   the paid tier). Stripe or similar; only after beta proves retention.
7. **iOS/native groundwork.** If POC showed iOS Safari is unusable, native
   app rises to the top of this list.

### ✅ Checkpoint 4 — QOL review (recurring)

After each QOL item ships: measure adoption, revisit the ordering above,
and re-check the two standing numbers (relay rate, TURN $/user) against the
pricing hypothesis.

---

## Standing risks carried through all phases

| Risk | Watched via | Trigger → action |
|---|---|---|
| Relay rate worse than modeled | Per-session telemetry from Checkpoint 2 on | Sustained >30% relay on paid tier → prioritize relay-aware quality (QOL #4) and IPv6 user guidance |
| Jellyfin breaks as an engine | Pinned versions + agent-controlled upgrades | Breaking API change we can't absorb → fallback engine (remux-only ffmpeg behind the same tunnel) |
| SCTP throughput ceiling | POC measurement, re-checked per browser release | 4K demand + ceiling holds → native app moves up |
| iOS browser degradation | POC + beta feedback | Significant iPhone user share → native app moves up |
| Railway limits (WS connection counts, egress pricing) | Dogfood monitoring | Outgrown → API/signaling are a plain container; portable anywhere |

## Explicitly out of scope until further notice

Cross-tenant sharing, multiple agents per tenant, wake-on-LAN, end-to-end
encrypted metadata, native apps (until a checkpoint promotes them), any
server-per-tenant cloud hosting (permanent no, per feasibility doc).

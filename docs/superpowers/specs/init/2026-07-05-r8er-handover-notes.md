# r8er — Handover Notes for the Implementing Agent

**Date:** 2026-07-05
**Audience:** the agent/developer picking up implementation (starting with the POC).
**Purpose:** context, fine print, and what-if hedges that didn't belong in the
formal docs but will save you time or prevent a wrong turn.

## Document authority order

1. `2026-07-05-r8er-implementation-plan.md` — **wins on phasing and stack.**
2. `2026-07-05-r8er-feasibility-design.md` — wins on architecture rationale,
   risks, privacy rules, cost model.
3. This file — background only; if it contradicts the above, the above win.

## How this project is being run (working style)

- The owner (Dennis) works in **ponytail mode**: laziest solution that works,
  stdlib/native/platform first, shortest diff, no speculative abstraction.
  Control-plane and media-management CRUD should stay *boring* — plain EF
  Core, no repository-pattern ceremony, no microservices. All cleverness
  budget goes to the streaming path (tunnel, signaling, agent).
- Decisions with business weight (pricing, product scope, provider choice)
  get **asked, not assumed**. Technical decisions with an obvious default get
  made and noted, not asked.
- Docs live in `docs/superpowers/specs/`, dated filenames.

## Decision history you can't see in the docs

These were explicit owner choices, not defaults — don't relitigate casually:

- **Go/Pion for the agent was chosen deliberately over .NET/SIPSorcery**,
  accepting a second language, because Pion is the battle-tested headless
  data-channel stack. If you're tempted to unify on .NET "for consistency,"
  that ship has sailed unless POC evidence says otherwise.
- **Cloud file storage was deferred entirely** (option "defer" chosen over
  both Firebase-storage and R2). Firebase is **auth only** right now. When
  cloud files arrive in QOL, the egress-cost argument (GCS ~$0.12/GB vs R2
  $0) was already made and points at R2 — re-decide with data, but that's
  the prior.
- **Jellyfin as bundled engine** (not a wedge product for existing Jellyfin
  users) — but the wedge remains the documented pivot if the market or the
  bundling fails.
- The feasibility doc originally had "cloud-only product first"; the owner
  inverted this — **home-media streaming is the product**, cloud is garnish.

## Technical fine print (things the plan says in one line that hide a day of work)

### The browser side of the tunnel
- The plan's Service Worker option comes with gotchas — know them before
  picking it:
  SWs don't control the page on first load until registered+activated;
  they can be killed and restarted (your data-channel handle doesn't live
  in the SW — it lives in the page; you'll likely relay SW→page via
  `postMessage`/`MessageChannel`, which adds a hop). Range request headers
  must be forwarded faithfully or seek silently breaks.
- **Strong alternative that avoids the SW entirely:** use **hls.js with a
  custom loader** — hls.js lets you plug in your own fragment/playlist
  loader, which can read straight from the data channel in page context.
  Since playback is Jellyfin **HLS** anyway, this may be strictly simpler
  than a SW. Evaluate BOTH in POC step 4; pick the dumber one that works.
  (SW is still needed if you ever want to proxy *arbitrary* Jellyfin API
  traffic transparently; for a curated API client, plain wrapped fetches
  over the data channel work without any SW.)
- iOS Safari: native HLS playback exists (no MSE needed) **but only from a
  URL**, which the data-channel approach can't provide without a SW — and
  iOS SW support inside standalone PWAs has its own quirks. Don't promise
  iPhone anything until POC data exists.

### Pion / data channel realities
- SCTP messages are limited — **chunk tunnel payloads (~16–64KB)** and
  reassemble; never send a whole video segment as one message.
- **Backpressure is your problem:** watch `bufferedAmount` /
  `OnBufferedAmountLow` on both ends; without flow control the agent will
  happily buffer a whole movie into memory. This is the main "novel"
  engineering in the tunnel — budget for it.
- Use **ordered, reliable** channels for HTTP semantics (default). Multiple
  parallel channels is the lever if one stream's head-of-line blocking hurts
  (e.g., separate channel for API traffic vs media segments).
- **The agent's DTLS certificate must be persistent.** Pion generates an
  ephemeral cert per PeerConnection by default — that silently breaks the
  fingerprint-pinning feature (feasibility doc, "Provable privacy").
  Generate the cert once at first run, store it next to the agent
  credential, and pass it via `SettingEngine`/certificate config on every
  connection. Cert rotation = a re-pairing event, by design.

### Jellyfin integration
- Bind Jellyfin to the **container network / localhost only**. The tunnel is
  the *only* path in. If Jellyfin is reachable on the LAN directly, fine for
  the user, but never expose it via UPnP or the agent's doing.
- The agent should own a Jellyfin **API key** (or admin user it provisions)
  and the r8er client should authenticate to *r8er*, not to Jellyfin — map
  r8er tenant members to Jellyfin users inside the agent, or run
  single-Jellyfin-user in MVP (tenant = household = one Jellyfin user is a
  legitimate ponytail simplification; per-member watch-state arrives with
  QOL multi-member work).
- **Pin the Jellyfin image version.** The agent controls upgrades. Never
  `latest`.
- **Licensing:** Jellyfin is GPLv2. Separate process over HTTP = mere
  aggregation = fine, including shipping both containers in one Compose
  file. Do NOT link Jellyfin code into the agent binary. Verify nothing in
  the distribution story crosses that line (this was flagged, not resolved).

### Control plane details
- Firebase token verification in .NET: `FirebaseAdmin` SDK or plain JWT
  validation against Google's JWKS. **Don't use the Firebase UID as your
  primary key** — own `users.id` (UUID) with a `firebase_uid` column, so an
  auth-provider migration later is a column swap, not a schema apocalypse.
- Put `tenant_id` on **every** tenant-owned table from day one and filter in
  a single place (EF global query filter). Tenant isolation bugs in a media
  product are catastrophic; make the safe path the default path.
- Cloudflare Realtime TURN wants **short-lived credentials minted per
  session via their API** — the control plane issues them when brokering a
  connection; never bake a static TURN secret into the client.
- Railway: WebSockets ride HTTP fine, but expect **idle timeout / restarts**
  — agents and clients must reconnect with backoff as a matter of course,
  not as error handling. Deploys restart the signaling broker; in-flight
  WebRTC sessions survive (media is P2P), only signaling reconnects.
- .NET 10 is the current LTS — fine. One web app hosts API + signaling WS;
  don't split services until something forces it.

### Telemetry is a deliverable, not an afterthought
The single most valuable output of POC and dogfood is the
**per-session connection record**: direct vs relay, IPv4/v6, candidate types,
throughput, session duration. A dumb Postgres table written by the signaling
broker + agent reports is enough. Every go/no-go checkpoint reads this table.

## What-if hedges

| What if… | Then… |
|---|---|
| Service Worker tunnel is flaky (iOS, PWA, SW lifecycle) | hls.js custom loader in page context — no SW needed for playback; curated API client uses plain wrapped fetches. |
| Data-channel throughput caps below ~10 Mbps in some browser | Accept 1080p cap for browser client (document it); parallel channels as second lever; native app (already Phase-2/QOL listed) is the real fix. Don't fight SCTP for 4K. |
| Jellyfin bundling fails (API, licensing, footprint) | Documented fallback: Go agent serves ffmpeg-remuxed fMP4/HLS behind the same tunnel. Keep the client's media interface Jellyfin-shaped-but-thin (a small adapter), so the swap touches one layer. |
| Cellular-to-home relay rate is bad (>30–40%) even with IPv6 | The economics question, not an engineering one: gate remote streaming behind paid tier earlier, and re-read the Jellyfin-wedge pivot in the feasibility doc before building more. |
| Cloudflare TURN pricing/terms shift | coturn on a cheap VPS (Hetzner) is the classic fallback; TURN attaches by config string, zero architectural coupling. Budget ~1 day. |
| Railway WS connection limits or pricing bite at beta scale | API+signaling is one plain container; it moves anywhere (Fly, Hetzner, GCP) with a DNS change. Don't pre-migrate; just don't take Railway-proprietary dependencies. |
| Firebase Auth becomes limiting (household members, claims) | Tenant membership lives in **Postgres**, not in Firebase custom claims — Firebase only answers "who is this," r8er answers "what may they touch." Keep it that way and Firebase stays swappable. |
| Owner's dev machine is Windows and agent targets Linux/NAS | Develop the Go agent against Docker Desktop; CI cross-compiles (Go makes this trivial). Test on a real NAS before Checkpoint 3 — filesystem paths, hardware transcode devices (`/dev/dri`) behave differently. |
| Jellyfin's transcode needs hardware accel on the user's box | Compose file must optionally pass through `/dev/dri` (Intel QSV) — make it a documented toggle, not automagic. Software-transcode fallback = loud warning in agent status page, not silent CPU meltdown. |
| Scope temptation: catalog sync, multi-member, sharing "while we're in there" | No. The plan defers them deliberately. MVP's library browsing goes through the tunnel; host offline = "host offline." Resist. |

## Known unknowns intentionally left open

- Tunnel shape final call (SW vs hls.js-loader vs both) — POC step 4/5.
- Jellyfin go/no-go — Checkpoint 1.
- Cloud storage provider — QOL, prior points at R2.
- Pricing numbers — business decision, constraints in feasibility doc.
- Signaling tenant-isolation design — written during MVP step 3, security-
  reviewed at Checkpoint 3.

## One-paragraph summary if you read nothing else

Build the POC in the plan's order and treat Checkpoint 1 as sacred: measure
cellular-to-home connectivity and prove seek-through-tunnel before writing
any product code. Keep the tunnel engine-agnostic (Jellyfin behind it is a
detail), keep CRUD boring, put `tenant_id` everywhere, chunk and
flow-control the data channel, mint TURN creds per session, and log every
connection attempt to Postgres — that table decides the company's economics.

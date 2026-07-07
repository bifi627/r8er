# POC Step 6 — Connectivity Spike (handoff)

Status doc for **Phase 0, step 6** of the implementation plan: measure the
real-world direct-vs-relay rate for the flagship path (**phone on cellular →
home agent**) and confirm TURN fallback. This is the instrument + the data so
far; **Checkpoint 1** is not yet closed — it needs a bigger sample.

_Last updated: 2026-07-07. Repo state: `feat: signaling poc part 8`._

## TL;DR

- **6a (instrument + local relay proof): DONE.** Telemetry sink → Postgres,
  a browser harness that classifies each session (direct/relay, IPv4/v6,
  negotiated path, throughput), verified locally against coturn.
- **6b (real field test): IN PROGRESS.** Deployed to Railway with Cloudflare
  TURN. **Both paths proven on real infra:** cellular UDP hole-punch went
  *direct* (row #4), Cloudflare relay carries a forced session (row #6).
- **Blocking Checkpoint 1:** only **1 meaningful away pair** collected so far.
  Need **≥10** (cellular ×N, friends' Wi-Fi, café) to state a real rate.

## What's proven (data so far)

Read live: `GET https://r8er.up.railway.app/telemetry/poc` (returns rows + a
`{total, direct, relay, failed}` summary).

| # | note | conn | path | throughput | RTT | meaning |
|---|------|------|------|-----------|-----|---------|
| 1–2 | home-lan | direct | host→host | ~330 Mbps | 0 ms | LAN baseline (trivial) |
| 3,5 | home-wlan-phone | direct | host→host | 209–240 Mbps | 4–29 ms | phone on home Wi-Fi = LAN (trivial) |
| **4** | **Mobile-phone (cellular)** | **direct** | prflx→srflx | **30 Mbps** | 67 ms | **cellular hole-punched through CGNAT — no relay** |
| **6** | home-wlan-phone (`&relay=1`) | **relay** | relay→prflx | 49 Mbps | 65 ms | **Cloudflare TURN relays correctly** (forced; excluded from rate) |

Takeaways:
- The **flagship worst case worked direct on the first cellular sample** — free,
  private (0 bytes through r8er/Cloudflare), 30 Mbps (comfortable 1080p). One
  sample only; do **not** anchor on it.
- The data-channel SCTP ceiling (~330 Mbps on LAN) is **not** the bottleneck;
  the network is. 4K verdict is bandwidth-bound, not stack-bound.
- Row #6's 49 Mbps relay is over **home Wi-Fi** legs, so it's optimistic — a
  real *cellular* relay will be slower (still ≥1080p expected).

## Operating the instrument

**1. Mint Cloudflare TURN creds (24h):**
```powershell
.\scripts\gen-turn-creds.ps1          # prompts for Key ID + API token
```
Prints the home-agent env + a bookmarkable phone URL; saves
`dev/.cloudflare-turn.json` (git-ignored). Re-run when creds expire.

**2. Run the home agent** (paste the script's `=== Home agent ===` block):
```
R8ER_SIGNAL_URL   = wss://r8er.up.railway.app/signaling/poc
R8ER_ICE_URLS     = turn:turn.cloudflare.com:3478?transport=udp,...(all Cloudflare URLs)
R8ER_ICE_USERNAME = <from script>
R8ER_ICE_CREDENTIAL = <from script>
go run ./cmd/agent      # from agent/
```
The agent must be the first peer in the room and stay running throughout.

**3. Gather sessions** — open the phone URL (`…/connect.html#…creds…`), set the
**note** per network, **Connect & measure** (~15 s each). **Leave force-relay
OFF** — the natural direct/relay outcome is the measurement. Label clearly:
`cellular-<carrier>-<place>`, `friend-<name>-wifi`, `cafe-<name>`.

**4. Read the results** — ask, or `GET /telemetry/poc`. Checkpoint-1 rate =
`direct / (direct + relay + failed)` over the **away** pairs (exclude LAN + the
forced `&relay=1` sanity row). The harness logs a `failed` row when no candidate
pair connects at all (e.g. UDP-blocked Wi-Fi that also defeats TURN) — the worst
outcome must count against the rate, not vanish.

**Local relay path (no cloud needed):** `docker compose --profile relay up -d`,
run the agent with `R8ER_ICE_URLS=turn:127.0.0.1:3478?transport=udp` +
`dev`/`dev`, open `http://localhost:5028/connect.html#…&relay=1`. coturn needs
`--external-ip=127.0.0.1` on Docker Desktop (already in compose) or the relayed
candidate is a black hole.

## Instrument architecture

- **Backend** (`backend/src/R8er.Api`, on Railway):
  - `/signaling/{room}` — WebSocket SDP/ICE relay (2 peers, unchanged since step 2).
  - `POST/GET /telemetry/poc` — insert one session row / list rows + summary.
    `Telemetry.cs`: raw **Npgsql** (no EF), `CREATE TABLE IF NOT EXISTS
    poc_connectivity`, conn string from `DATABASE_URL` (Railway) or
    `ConnectionStrings:Postgres` (local). Schema init is **best-effort** — a DB
    outage logs a warning and never takes signaling down.
  - `/connect.html` — the harness, served static from `wwwroot/` so a phone can
    load it same-origin (file:// is desktop-only).
- **Agent** (`agent/cmd/agent`): `iceServers()` = Google STUN + optional TURN
  from `R8ER_ICE_*` env.
- **Harness** (`wwwroot/connect.html`): origin-aware defaults (signal + telemetry
  from `location`); hash params `signal,tele,note,turn,user,cred,relay`. Reuses
  the step-3 "throughput" flood for Mbps, then `getStats()` to classify.

**Classification rules (learned the hard way):**
- **Relay** ⇔ selected local candidate has `relayProtocol` set, or either end's
  `candidateType === 'relay'`. On a relay-only path Chrome relabels the winning
  local candidate as `prflx` and redacts its address, so `relayProtocol` (spec:
  set only on TURN-obtained candidates) is the reliable signal — **not**
  candidateType or address matching.
- **IP family** from the selected candidate's address; when redacted (host
  mDNS / prflx), fall back to a gathered `relay` then `srflx` local candidate
  (its public-mapped address reveals the client's IPv4/v6 egress).
- **Throughput** = peak Mbps over the 15 s agent→browser flood; avg also logged.

## Key decisions / gotchas (don't relearn these)

- **Minimal-API cancellation:** inject a `CancellationToken` parameter; do **not**
  read `HttpContext.RequestAborted` inline — that produced empty `200`s.
- **coturn on Docker Desktop** advertises its container IP (unreachable from host
  peers); `--external-ip=127.0.0.1` maps relays to the published ports.
- **Byte-transparent tunnel** (step 4): agent uses `DisableCompression` so Go's
  transport doesn't gzip-rewrite bodies / drop Content-Length.
- **CORS** is wide-open on the backend (POC, no auth) — harmless now that the
  harness is served same-origin; scope it when telemetry graduates.

## What's left for Checkpoint 1

- [ ] **≥10 away network pairs** (the current gap) → real direct-vs-relay rate.
      Cellular is the one that matters; also friends' + public Wi-Fi.
- [ ] **Sustained 1080p + a subtitle track over the away path**, driven through
      the real player (`dev/play.html` via the tunnel). Note: current dev-media
      fixtures are 720p / no subtitles — need real 1080p+subtitle media.
- [ ] **4K verdict** (feasible / needs native app) — bandwidth-bound; record the
      real cellular-direct throughput distribution.
- [ ] **Tunnel approach confirmed on target browsers** (Checkpoint-1 exit
      criterion, easy to lose): Android Chrome + desktop verified; **iOS Safari
      status documented honestly** — hls.js needs MSE, which iOS Safari only has
      via ManagedMediaSource (17.1+). Untested so far.
- [ ] **Step 7 — codec census:** script over 2–3 real libraries to confirm
      Jellyfin's transcode coverage.
- [ ] **Jellyfin verdict** (bundle vs. fallback engine) per the checkpoint list —
      including the two unmeasured inputs: idle footprint on target hardware and
      the GPLv2 distribution check (flagged, not resolved, in the handover notes).

## Caveats

- 1 cellular sample; the rate is unknown until the sample grows. Feasibility doc
  explicitly warns cellular-to-home direct rate is unmeasured — treat it so.
- Relay throughput (#6) measured over Wi-Fi legs, not a real cellular relay.
- Real-media 1080p/subtitle/4K over the *away* path not yet driven end-to-end
  (only synthetic throughput + a LAN HLS seek from step 5).
- POC code is throwaway (owner-confirmed): no auth/tenancy, hardcoded room `poc`,
  open CORS, short-lived creds pasted by hand. MVP promotes signaling into the
  tenant-isolated broker and telemetry into a real EF entity.

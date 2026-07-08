# POC Step 6 — Connectivity Spike (handoff)

Status doc for **Phase 0, step 6** of the implementation plan: measure the
real-world direct-vs-relay rate for the flagship path (**phone on cellular →
home agent**) and confirm TURN fallback. **The connectivity criterion of
Checkpoint 1 is now MET** (see verdict); the remaining checkpoint items
(real-media 1080p/subtitle, 4K, codec census, iOS Safari) are still open.

_Last updated: 2026-07-08. Repo state: `feat: signaling poc part 8`._

## TL;DR

- **6a (instrument + local relay proof): DONE.** Telemetry sink → Postgres,
  a browser harness that classifies each session (direct/relay, IPv4/v6,
  negotiated path, throughput), verified locally against coturn.
- **6b (real field test): DONE — 14 away sessions across 9 networks.**
  **Direct rate 86% (sessions) / 89% (distinct networks); cellular 100% direct;
  TURN fallback fired naturally on the one network that blocked hole-punch.**

## Verdict (Checkpoint-1 connectivity criterion: MET)

Read live: `GET https://r8er.up.railway.app/telemetry/poc` (returns rows + a
`{total, direct, relay, failed}` summary). Rate excludes `home-*`/LAN rows and
the forced `&relay=1` sanity row (#6).

| Metric | Result |
|---|---|
| Away sessions | 14 → **12 direct / 2 relay / 0 failed = 86% direct** |
| Distinct away networks | 9 → **8 direct / 1 relay = 89%** |
| **Cellular paths** (Mobile-phone ×N + hotspots, #4/10/11/16/18/19/22) | **all direct, 0 relay** |
| TURN fallback | **Confirmed naturally** — Dehner garden-center guest Wi-Fi behind a T-Mobile AP (#14/#15) was the *only* network that blocked hole-punch; both relayed cleanly via Cloudflare at 37–40 Mbps |

Takeaways:
- **The flagship path (cellular → home) went direct on every sample.** Free,
  private (0 bytes through r8er/Cloudflare), 5–32 Mbps — comfortable 1080p.
- **TURN is a real, exercised fallback, not a hypothesis** — one hostile guest
  network (captive T-Mobile AP) needed it and it worked.
- **Connectivity ≠ bandwidth (record this):** #23 `Vs-free-wlan` connected
  *direct* but at **0.79 Mbps / 130 ms** — a public Wi-Fi with a tiny pipe.
  Some networks won't sustain 1080p (~5 Mbps) regardless of path. That's an
  argument for adaptive bitrate / transcode at MVP, not a tunnel problem.
- The data-channel SCTP ceiling (~330 Mbps on LAN) is **not** the bottleneck;
  the network is. 4K verdict is bandwidth-bound, not stack-bound.

## 4K verdict (bandwidth-bound: PASS via adaptive bitrate)

**Method:** no separate 4K playback run — the plan asks this be settled from the
"real cellular-direct throughput distribution," and every input needed is already
proven. The stack is not the limit (SCTP tops ~330 Mbps on LAN, 257 Mbps over
Wi-Fi — rows #8/#13 — vs. ≤25 Mbps for any 4K stream), and both media paths that
4K can take through the tunnel are proven: **remux/direct-play** (1080p+subs,
done) and **transcode** (HEVC→H.264, step 7a). So 4K reduces entirely to: does the
away link carry the bitrate, and if not, does it degrade gracefully.

**Cellular-direct throughput (the flagship path, n=7):**
5.3 · 12.6 · 18.6 · 28.3 · 29.9 · 32.3 · 46.9 Mbps → **median ~28, min 5.3, max 47**.

| Stream | Typical bitrate | Cellular samples that sustain it |
|---|---|---|
| 1080p H.264 | ~5–8 Mbps | 6–7 / 7 |
| 4K HEVC (direct-play) | ~10–15 Mbps | **6 / 7** (all but the 5.3 Mbps work-site sample) |
| 4K H.264 | ~15–25 Mbps | ~5 / 7 (12.6 marginal, 5.3 no) |

**Verdict:**
- **4K is feasible on most measured cellular links** — a median cellular path
  (~28 Mbps) comfortably carries 4K HEVC and clears 4K H.264. The tunnel itself
  never limits it.
- **It is not universal, and that's fine:** the answer to a weak link (the 5.3 Mbps
  work site, or #23 `Vs-free-wlan` at 0.79 Mbps) is **adaptive bitrate — Jellyfin
  transcodes the 4K source down to a rendition the link can hold.** That mechanism
  is now proven end-to-end by step 7a (live transcode over the tunnel, with an
  ABR-shaped ladder). Result on a thin pipe is lower resolution, **not a failure**.
- **Native app is a 4K-quality QOL upgrade, not a Checkpoint-1 requirement:** it
  would direct-play 4K HEVC without a browser MSE dependency and cover codecs the
  browser can't decode. Browser-first stands; native stays deferred.

**Caveat:** verdict is from the bandwidth distribution + the proven remux/transcode
paths, not from streaming an actual 2160p file (fixtures top out at 1080p). That is
exactly the plan's ask ("bandwidth-bound; record the distribution"); a real 4K-file
run is a cheap MVP confirmation, not a Checkpoint-1 gate.

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

- [x] **≥10 away network pairs → real direct-vs-relay rate.** DONE: 14 sessions
      across 9 networks, 86% direct / cellular 100% direct, TURN fired once. See
      verdict above.
- [x] **Sustained 1080p + a subtitle track over the away path — DONE.** Driven
      end-to-end through the tunnel on mobile: **1080p held** (306 s played, ~43 s
      buffered ahead, 3 startup stalls totalling ~1 s, seek-to-5:00 in 594 ms) and
      **subtitles render** (WebVTT rendition fetched over the data channel,
      `mode=showing`). Two noted non-blockers: hls.js cold-start skips subtitle
      segment 0 (the @2 s cue) — its scheduler aligned to the forward buffer edge
      during startup stalls, not a tunnel fault, a tuned MVP player avoids it; and
      a transient `STUN 701` DNS hiccup that ICE recovered from (add
      `stun1–4.l.google.com` if it ever persists on an away network). Instrument
      details below.
      - `scripts/gen-test-media.sh` now emits `h264-1080p-subs.mp4` (**CBR ~6 Mbps**
        1080p, so it stresses the network — testsrc would otherwise compress to
        ~1 Mbps) + a sidecar `.srt`.
      - `setup-jellyfin.sh` indexes the sidecar as a subtitle stream and prints a
        phone play URL (`/play.html#item=…&key=…&subs=…`). It now selects fixtures
        by **file Path** — Jellyfin's metadata matcher collapses all H.264 clips
        to one Name, so Name-based selection aliased them.
      - Verified by direct curl (no browser): `master.m3u8?SubtitleStreamIndex=0&`
        `SubtitleMethod=Hls` returns a `TYPE=SUBTITLES` WebVTT rendition alongside
        a `RESOLUTION=1920x1080` video with `audioCodec=copy` → **remux, not a
        transcode** (network-bound, the intended test).
      - `play.html` moved to `wwwroot/` (phone loads it same-origin, like
        connect.html), turns on the hls.js subtitle track, and **measures** stalls
        / stalled time / buffer-ahead / subtitle-cue count so "sustained" is data,
        not a claim. Relay-needing networks: append `&turn=&user=&cred=`.
      - **Pending:** open the phone URL on the away path, confirm smooth 1080p +
        visible subtitles + seek. **Needs a Railway redeploy first** (play.html is
        a new static file) — human-approved.
- [x] **4K verdict — DONE (bandwidth-bound: PASS via ABR).** Feasible on most
      measured cellular links (4K HEVC clears 6/7, median ~28 Mbps); weak links
      degrade gracefully via transcode-down (proven by 7a), not failure; native
      app is a QOL upgrade, not a gate. Full analysis + distribution table in the
      "4K verdict" section above.
- [ ] **Tunnel approach confirmed on target browsers** (Checkpoint-1 exit
      criterion, easy to lose): Android Chrome + desktop verified; **iOS Safari
      status documented honestly** — hls.js needs MSE, which iOS Safari only has
      via ManagedMediaSource (17.1+). Untested so far.
- [x] **Step 7a — transcode over the tunnel: DONE.** Drove a live HEVC+AC3 →
      H.264+AAC re-encode through the data channel on desktop Chrome **and**
      mobile — we had only proven remux/copy before. Verdict: the transcode path
      is codec-transparent to the tunnel (byte stream is identical to a remux;
      the data channel doesn't know or care it's a re-encode). Seek worked
      (`SEEK OK`), buffered cleanly at 720p. **Relay leg deliberately NOT
      re-tested** — relay is transport-layer and codec-agnostic; step 6 already
      proved TURN carries the tunnel, so transcode-over-relay is composition, not
      a new risk. Server-side proof: `master.m3u8` on the `hevc/ac3` source with
      `videoCodec=h264&audioCodec=aac` emits an `avc1/mp4a` variant, and the
      served `.ts` ffprobes as `h264,aac` (source is `hevc,ac3`). `#vb=<bits/s>`
      hash param (added to `play.html`) forces a realistic transcode bitrate —
      `vb=4000000` gives full 1280×720 @ ~1.7 Mbps (default was 416×234).
- [ ] **Step 7 — codec census (remaining):** ffprobe over 2–3 *real* user
      libraries to inventory codecs/containers and size how much a remux-only
      fallback would cover. Needs real media (dev fixtures are synthetic). The
      transcode-path risk itself is retired by 7a above.
- [ ] **Jellyfin verdict** (bundle vs. fallback engine) per the checkpoint list —
      including the two unmeasured inputs: idle footprint on target hardware and
      the GPLv2 distribution check (flagged, not resolved, in the handover notes).

## Caveats

- Sample is 9 distinct networks / 14 sessions — enough to close the checkpoint
  criterion, not a population statistic. All one household, one phone, one
  region (DE), summer. The 86–89% rate is directional, not a guarantee.
- The relay samples (#14/#15) are a real away network but still Wi-Fi legs; a
  cellular-to-relay worst case wasn't captured (cellular always went direct).
- Real-media 1080p/subtitle/4K over the *away* path not yet driven end-to-end
  (only synthetic throughput + a LAN HLS seek from step 5).
- POC code is throwaway (owner-confirmed): no auth/tenancy, hardcoded room `poc`,
  open CORS, short-lived creds pasted by hand. MVP promotes signaling into the
  tenant-isolated broker and telemetry into a real EF entity.

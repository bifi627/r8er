# r8er — Agent onboarding + device pairing (MVP items 2 + 3) — design

> **Vocabulary note (2026-11-02):** written before the rename (TECH_DEBT T9). "Agent" / `Device` / `devices` here = **gateway** / `Gateway` / `gateways` today; a "browser device" and its "device key" = **client** / client key; `/agents/*` and `/devices` routes = `/gateways/*`, `/gateways`. Current contract: `docs/protocol.md`.

Date: 2026-11-01 · Status: draft for owner review
Supersedes the pairing mechanics sketched in the implementation plan (item 2:
"QR with claim code + fingerprint") — see "Changes vs the plan" below.

## Intent

A household owner installs the r8er agent on their NAS/PC and streams from
their browsers anywhere. Two guarantees:

1. **Ownership** — an agent belongs to exactly one tenant; the server only ever
   connects a browser to agents of its own tenant.
2. **Provable privacy** — "we *can't* look", not "we won't look". A
   compromised control plane may deny service but can never read a stream:
   neither by impersonating the agent to a browser, **nor by connecting its own
   browser to the agent**. The plan's original design pinned only one direction
   (browser pins agent); that leaves the second attack open. This design pins
   **both directions**.

## Two flows

**Flow 1 — Onboarding** (once per agent, from any network): binds a new agent
to the tenant. **Flow 2 — Device pairing** (once per browser/device, on the
home LAN): mutual pinning between one browser and the agent.
Separate flows; the only link is that onboarding ends with a "Pair this
device" prompt that starts flow 2 in the same browser.

### Flow 1 — Onboarding

1. Signed-in user clicks **Add agent**. Server creates a one-time
   **enrollment token** (random, ≥128 bit), stores only its SHA-256 hash with
   `tenant_id` and `expires_at = now + 15 min`.
2. Web app shows the install command with the token embedded.
3. User runs it. On first start (empty state dir) the agent generates its
   **persistent DTLS certificate** (see "Agent certificate") and stores it.
4. Agent calls the server's enrollment endpoint with token + DTLS fingerprint +
   hostname (default device name). Server, in one transaction: checks token
   hash exists, unexpired, unused → marks used → creates the `devices` row
   (tenant from the token, `dtls_fingerprint`, name) → mints a **long-lived
   agent credential** (random ≥256 bit), stores its SHA-256 hash in
   `agent_credential_hash`, returns credential + device id once.
5. Agent persists credential, then holds an authenticated WebSocket to the
   server and **heartbeats** (every 30 s): server updates `last_seen_at` and
   the agent's advertised **LAN URL**.
6. Devices list shows the agent online (`last_seen_at` within 90 s) with an
   **Open agent page** link (LAN URL) and **Pair this device**.

If the state dir already holds a credential, the agent ignores any enrollment
token (token in shell history / `docker inspect` is spent and harmless).

### Flow 2 — Device pairing

1. On the new device (signed in, on the home LAN) the user presses **Pair this
   device**. The browser creates its **device key** — a non-extractable
   WebCrypto ECDSA P-256 key pair — and stores it in IndexedDB (see "Browser
   identity").
2. Browser connects to the agent over WebRTC (ordinary per-session DTLS
   certificate) via the tenant-scoped broker (server only brokers to agents of
   the caller's tenant).
3. Both sides compute a **6-digit comparison code** from the agent's DTLS
   fingerprint, the browser's device-key fingerprint, and committed random
   nonces (see "Comparison code"), and display it: browser in the web app,
   agent on its local page ("new device 'Dennis's iPhone' wants to pair ·
   482 913").
4. The web app shows **Open agent page** (LAN URL). User checks the codes match
   and presses **Approve** on the agent page.
5. Agent adds the browser's **device public key** to its **allowlist** and
   tells the browser over the data channel; browser stores the **agent pin**
   (agent DTLS fingerprint) in IndexedDB (keyed by device id).

If the server substituted either side (MITM), the two codes differ — so the
code comparison authenticates both directions out-of-band, no QR needed.

**Every later connection:** browser rejects any agent whose DTLS fingerprint ≠
its pin, before sending a single request, and shows a loud warning. The agent
demands a signature from the browser's device key bound to *this* session
(see "Connection auth") and rejects any browser whose key isn't on its
allowlist (unless a pairing request is in progress and gets approved).

## Changes vs the plan (decided with owner 2026-11-01)

- **Mutual pinning**, every device pairs (plan: one-way, client pins agent).
  Browser side pins a **WebCrypto device key**, not a browser certificate.
- **Enrollment token in the install command** replaces "agent prints claim
  code + QR, user types it in" (no code typing, no IP hunting; GitHub-runner /
  Tailscale style).
- **Comparison code replaces the QR** as the out-of-band channel.
- **Agent advertises its LAN URL** so the web app links to the agent page.
  Privacy cost: r8er learns a private LAN address — low-value metadata,
  accepted.
- **One member per tenant** stays (roommate/household members = Phase 2).
- **Approval happens on the agent's local page**, never from the server or
  another browser: only someone at your hardware can let a device in.

## Goals / non-goals / anti-goals

Goals: flows 1 + 2 end-to-end; pins enforced on every connection in both
directions; tenant isolation on onboarding, heartbeat, and brokering —
explicitly tested; repeatable onboarding on one dev machine.

Non-goals (out of scope here):
- Multiple members per tenant (Phase 2).
- Server-side device revocation, signaling rate limits, short-lived TURN creds
  (item 6). *In:* removing a paired browser from the agent page's list.
- Auto-update, Compose packaging, published agent image (item 4).
- Certificate rotation — rotation = re-onboard / re-pair for now; automatic
  rollover over the pinned channel is planned for item 4 (TECH_DEBT T12).
- Fixing the served-JavaScript limit (below) — documented only.

Anti-goals (failure even if it "works"):
- Any path where the server alone can make an agent accept a browser, or make
  a browser accept an agent.
- An ephemeral agent certificate or browser device key (pins silently break on restart).
- Tenant-filter bypasses beyond the two named below.
- Plaintext secrets at rest on the server (token, credential: hashes only).

## Threat model

- **Control plane untrusted for confidentiality**, trusted for availability.
- **Home LAN trusted**: anyone who can open the agent page can approve a
  device — but still needs to be signed in to the tenant's account to be
  brokered at all.
- **Known limit, stated openly:** the web app's JavaScript is served by r8er.
  A compromised server could ship JS that exfiltrates streams. Pinning closes
  the network path, not this one (fix = installable/signed client, post-MVP).
  Say so in user-facing privacy copy; don't overclaim.

## Components

### Server (.NET, `backend/`)

- **Migration #2**: `enrollment_tokens` (id, tenant_id, token_hash unique,
  expires_at, used_at, created_at) — tenant-owned, `ITenantOwned`. `devices`
  gains `lan_url` (nullable). `agent_credential_hash` gets a unique index.
- **Endpoints**
  - `POST /agents/enrollment-tokens` (Firebase auth) → `{ token, expiresAt,
    installCommand }`.
  - `POST /agents/enroll` (unauthenticated; the token *is* the auth) →
    `{ deviceId, credential }`.
  - Agent WebSocket (Bearer credential) — heartbeat in PR A; carries
    signaling in PR B.
  - `GET /devices` adds `online`, `lanUrl`.
- **Tenant-filter exceptions (exactly two, each commented + tested):**
  enrollment-token lookup by hash and device lookup by credential hash run
  without a tenant context (the caller *is* establishing it) →
  `IgnoreQueryFilters()` there and nowhere else.
- **Install command** is built from the server's configured public URL
  (local `:5028` in dev, Railway domain in prod) — no hardcoded host. PR A
  shows the env-var form (`R8ER_SERVER_URL`, `R8ER_ENROLL_TOKEN`) plus a
  `go run` line for dev; the `docker run`/Compose form arrives with item 4's
  published image. *(my call)*

### Agent (Go, `agent/`)

- **State dir** `R8ER_STATE_DIR` (default `./r8er-state`; a Docker volume
  later): DTLS cert + key, credential, device id, allowlist (JSON). Delete the
  dir = brand-new agent.
- **Agent certificate**: generated once; long expiry (Pion's
  `GenerateCertificate` default is short — build via `x509` template, e.g. 10
  years) and loaded on every PeerConnection via `Certificates`.
- **Local page** on `R8ER_PAGE_PORT` (default 8097; Jellyfin owns 8096): name,
  online state, own fingerprint; PR B adds pending approvals + paired-device
  list with Remove. Plain `net/http` + one HTML template.
  - **Must reject cross-site requests**: state-changing POSTs check `Origin`
    equals the page's own origin, and the `Host` header must be an IP literal
    or the configured LAN host (blocks drive-by approval from a malicious
    website and DNS rebinding).
- **LAN URL**: first private non-loopback IPv4 + page port, overridable via
  `R8ER_LAN_URL` (needed inside Docker, where the container IP is useless).
- Fixes the stale config names (TECH_DEBT T6) as part of the new config
  surface.
- Verifies browser signatures with stdlib `crypto/ecdsa` (P-256). WebCrypto
  emits raw 64-byte `r‖s` (IEEE P1363), not ASN.1 — convert via `big.Int`
  (no new dependency).

### Browser (React, `frontend/`)

- Devices page: Add agent (token + command, polls until the agent appears),
  online state, Open agent page, Pair this device.
- Pairing view: comparison code, waiting-for-approval state, success/failure.
- IndexedDB: own device key (`CryptoKey`, non-extractable, structured-cloneable)
  + agent pins per device.

### Browser identity (decided with owner 2026-11-01)

The browser's long-term identity is a **WebCrypto signing key**, not its
WebRTC certificate. Reasons: `RTCCertificate` expires (~30 days by default →
would need a rollover mechanism) and its IndexedDB persistence on iOS Safari
is unverified; `CryptoKey` storage in IndexedDB is the spec'd, widely
supported path, has no built-in expiry, can't be read out even by script on
the page, and maps 1:1 onto native keystores later (iOS Secure Enclave and
Android Keystore are P-256-native) if the native app gets promoted.

- `crypto.subtle.generateKey({name:"ECDSA", namedCurve:"P-256"}, false, ["sign"])`
  once per browser profile; the public key is exported as SPKI.
- **Device-key fingerprint** = `sha-256 AA:BB:…` over the SPKI bytes (same
  textual form as DTLS fingerprints).
- The DTLS certificate is per-session and throwaway (browser default).
- Rotation/loss (site data cleared, Safari eviction) = re-pair.

### Connection auth (every connection, PR B)

On a `auth` data channel right after DTLS is up:
1. Agent → browser `{type:"challenge", n: hex(32 random bytes)}`.
2. Browser → agent `{type:"auth", spki: base64, sig: base64}` where `sig` signs
   `utf8("r8er-auth-v1|" + agentDtlsFp + "|" + browserDtlsFp + "|" + n)` with
   ECDSA P-256 / SHA-256. Both DTLS fingerprints are those of *this* session,
   so a MITM (who terminates two different DTLS sessions) can't relay a
   valid signature.
3. Agent verifies the signature, then checks `fingerprint(spki)` is on the
   allowlist → serve tunnel; otherwise only the `pair` channel (see "Connection
   gating").

The `"r8er-auth-v1"` purpose label and the fresh challenge prevent replay and
cross-protocol reuse. One shared test vector (fixed key, fps, nonce → valid
signature verifies) in `protocol.md`, asserted in Go and TS.

### Comparison code (contract — goes in `docs/protocol.md`)

**Why a commitment step (my call, from the builder review):** a code derived
from the fingerprints alone is grindable. A malicious server knows the agent
fingerprint (stored at enrollment) and sees the browser's in the offer, so it
can generate ~10⁶ fake certificates offline until both sides show the same 6
digits — exactly the "server alone makes the agent accept a browser" anti-goal.
Fix = commit-then-reveal random nonces (ZRTP / Bluetooth numeric-comparison
pattern), so a MITM gets **one guess**, success probability 10⁻⁶.

On the `pair` data channel, after DTLS is up:

1. Browser → agent: `{type:"commit", h: hex(SHA-256(nB)), name, spki, sig}`
   (nB = 32 random bytes; name = device label; `spki`/`sig` = device public
   key + session-bound signature as in "Connection auth", over a challenge the
   agent sent first on the channel).
2. Agent → browser: `{type:"nonce", n: hex(nA)}` (nA = 32 random bytes,
   sent only after the commit arrives).
3. Browser → agent: `{type:"reveal", n: hex(nB)}`; agent checks it against
   `h`, else aborts.
4. Both compute
   `code = uint32_be(SHA-256(utf8(agentFp + "|" + browserKeyFp + "|" + hex(nA) + "|" + hex(nB)))[0..4]) mod 1_000_000`,
   zero-padded to 6 digits. Fingerprints use the form `sha-256 AA:BB:…`
   (upper-case hex). `agentFp` is the agent's DTLS fingerprint from the
   *negotiated* connection (browser: remote SDP `a=fingerprint`, which the
   browser itself verifies against the actual DTLS cert). `browserKeyFp` is
   the device-key fingerprint; the browser proves it holds that key with the
   session-bound signature from "Connection auth" (sent in the `commit`
   message), so the agent knows the key is the real peer of this session.
5. On Approve: agent → browser `{type:"approved"}` (or `{type:"rejected"}`),
   agent persists the allowlist entry, browser persists the pin.

One shared test vector (fixed fps + nonces → code) in `protocol.md`, asserted by
both Go and TS tests. Exact envelope finalized in `protocol.md` with PR B.

### Connection gating (my call)

Application-layer, not at the DTLS handshake: the agent accepts the DTLS
handshake from any brokered browser (the broker already limits callers to the
tenant), then runs "Connection auth":
- **Device key on the allowlist** (valid session-bound signature) → serves
  tunnel channels as today.
- **Not on it** → serves only the `pair` channel; every other channel is closed
  immediately. An incoming pairing request appears on the agent page as
  pending — no separate arming step (the user pressing "Pair this device" *is*
  the request). Pending requests expire after 5 min; at most 3 pending at once.

### Concurrent sessions (my call)

The agent serves **several browsers at once** (phone pairing while the TV
streams is the normal case). The broker envelope carries a `session` id; the
agent routes per session over its single authenticated WS and keeps one
PeerConnection per session. Designed into the broker messages from PR B's start.

## Delivery: one design, two PRs

| PR | Scope |
|---|---|
| **A — Onboarding (item 2)** | Migration #2, enrollment token + enroll endpoint, persistent agent cert + state dir, authenticated agent WS + heartbeat + LAN URL, Devices list online/offline + Open agent page, bare local agent page, dev knobs. POC room signaling untouched. |
| **B — Broker + device pairing (item 3)** | Tenant-scoped broker with session routing (browser auth: Firebase token as first WS message, not a query param — keeps it out of logs), browser device key + connection auth, commit/reveal comparison code, approve on agent page, allowlist, pins enforced both directions, isolation tests. **Done** = a paired browser profile fetches one tunneled HTTP request through a pinned connection; an unpaired or wrong-pin peer is refused. Telemetry graduates to a tenant-scoped EF entity (TECH_DEBT T5) with the pinned session as its producer. |

**POC paths (my call):** `/signaling/{room}`, the POC agent mode, and the POC
harness pages (`play.html`, `connect.html`, `dev/*.html`) stay **untouched
until item 5** replaces them with the real streaming client — so streaming
never regresses in between. They're an unauthenticated relay with no tenant
data, so they don't weaken the new flows. Item 5 deletes them; log it in
`TECH_DEBT.md` with PR B.

## Dev environment

- Multiple agents on one machine: one state dir + one page port each.
- Re-onboard: delete the state dir; `scripts/dev.ps1 -ResetAgent` does it.
- Multiple "devices": separate browser profiles (device key is per profile);
  incognito forgets its key on close → re-pairing test. A second emulator
  account in another profile = cross-tenant test.
- **WebCrypto needs a secure context** (HTTPS or `localhost`). Desktop dev on
  `localhost:5173` works. A real phone **cannot** use `http://<pc-ip>:5173`
  (no `crypto.subtle` there) — test phones against the Railway dev deployment
  (HTTPS). The agent's local page is plain HTTP and needs no WebCrypto.

## Testing

- **Reqnroll (server behaviors):** token single-use, expiry, credential auth
  rejects unknown/garbage, heartbeat updates only its own device, tenant B
  never sees tenant A's device; PR B: broker refuses cross-tenant targets.
- **Go:** cert stable across restarts (same fingerprint after reload),
  enrollment skipped when state exists, comparison-code test vector, page
  rejects cross-origin POST.
- **TS:** comparison-code and connection-auth test vectors (TS signs, Go
  verifies the same vector — catches the P1363/ASN.1 mismatch).
- **Driven by hand (done criterion):** onboard an agent locally, see it go
  online; PR B: pair two browser profiles, show a mismatched-pin rejection.

## Docs in the same diff

`docs/protocol.md` (agent WS, enroll API, comparison code, pairing messages),
`docs/tools.md` (enrollment token + agent credential: what they are, how to
rotate = re-onboard), `CLAUDE.md` current state, `TECH_DEBT.md` (T5/T6 rows
deleted when paid), plan item 2 text pointing here.

## Open risks / verify early

- **Safari storage eviction:** Safari may delete a site's script-written
  storage (IndexedDB) after ~7 days without a visit, unless added to the Home
  Screen → device key lost → re-pair. Surface it clearly ("this device needs
  re-pairing"); verify during the open iOS device test.
- **Site data cleared** → device must re-pair. Safe, slightly annoying.

## Left to the builder

Endpoint/route naming, WS message envelope details for heartbeat, exact
online threshold tuning, local page styling, IndexedDB schema, Go package
layout (the POC `main.go` may split; keep it boring).

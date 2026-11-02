# Tech Debt

Deferred hardening, test-harness robustness, and design smells accepted on
purpose. Defects → `BUGS.md`, unbuilt wants → `IDEAS.md`. Clear an item by
fixing it and deleting its section (git holds the history). No numbers —
reference items by heading.

### Token minter masks real HTTP failures
**Area:** tests / TokenMinter.cs

Emulator error-body parsing assumes an `error` JSON shape; a real HTTP failure
(5xx, non-JSON) could be masked. Guard with `TryGetProperty` / `EnsureSuccessStatusCode()`.

**Pay down:** next time the token minter is touched, or if a test fails with a confusing parse error.

### POC telemetry sink is raw Npgsql
**Area:** control-plane / Features/Poc/Telemetry.cs

POC connectivity table is raw Npgsql, no EF/migrations, hardcoded — throwaway
spike sink. Was to graduate to a tenant-scoped EF entity in PR B; deferred: the
pinned session only tunnels a test request today, so there is no real stream to
measure yet.

**Pay down:** item 5 (streaming client), with the player as producer; deleted in that PR.

### POC paths still live
**Area:** POC paths

`/signaling/{room}`, `agent/internal/poc` (+ POC mode in `main.go`),
`wwwroot/{play,connect}.html`, `dev/*.html` stay so streaming never regresses
before the real client. Unauthenticated relay, no tenant data.

**Pay down:** item 5 replaces and deletes them.

### Broker registry is in-memory
**Area:** control-plane / Features/Broker

Broker registry is in-memory: correct only with a single API instance (Railway today).

**Pay down:** second API instance → Postgres LISTEN/NOTIFY or Redis.

### Agent cert has no rotation
**Area:** agent / cert rotation

Agent DTLS cert is 10 years with no rotation (spec non-goal: rotation =
re-onboard + re-pair). A once-a-decade manual step will be forgotten, and Pion
refuses an expired cert → agent down at expiry. Plan (SSH UpdateHostKeys-style):
~1-year cert; ~60 days before expiry the agent pre-generates the next cert
(state dir) and sends `{type:"next-pin", fp}` on the `auth` channel *after* the
old pin verified; browser stores it as a second pin; agent switches at expiry;
reports the new fp to the server (display only). Browsers unused for the whole
window re-pair (= today). ~40 lines Go + ~15 TS + protocol.md message + Go/TS
test. Keep 10 years until this exists.

**Pay down:** item 4 (productized agent, alongside auto-update).

### Rename agent/device → server/client
**Area:** naming / whole repo

Owner-decided vocabulary (2026-11-01): **server** = the home box serving media
(today: "agent" / `Device`), **client** = a paired browser consuming it (PR B
spec: "browser device"). "Agent" is ambiguous with AI agents; "device" covers
both sides. Rename spans `agent/` dir + Go module, `devices` table (rename
migration), `Device` entity, `/agents/*` + `/devices` routes,
`features/devices`, live docs (CLAUDE.md, protocol.md, tools.md, pairing spec).
Collision to resolve: the control plane is also called "server" today
(`R8ER_SERVER_URL`, backend) — needs its own name (e.g. control plane,
`R8ER_CONTROL_URL`). Dated POC/init docs stay as records + a glossary note.

**Pay down:** no fixed trigger — own `chore/rename-server-client` branch whenever there's a quiet gap; cheaper the earlier.

### npm audit: frontend grpc-js (via firebase)
**Area:** frontend / npm audit

4 high `@grpc/grpc-js` advisories (GHSA-m9gg-hp2v-232j, GHSA-f596-whhp-79r4;
fixed in ≥1.13.6). Transitive via `firebase` → `@firebase/firestore`, which pins
`~1.9.0` even at latest (firebase 13.1.0 / firestore 4.19.0). Not exploitable
here: both are gRPC-*server* issues, we never import Firestore, and the built
bundle contains no grpc code. No `overrides` forced into a module we don't load;
`npm audit fix --force` would downgrade to firebase 9.

**Pay down:** when a firebase release moves firestore off grpc-js 1.9 (check at each deps sweep), or immediately if we start using Firestore.

### npm audit: e2e glob/minimatch (via testcontainers)
**Area:** e2e / npm audit

7 moderate entries = 2 advisories: glob GHSA-3g3m-2fjr-6pw4 (AbortSignal timeout
bypass) and minimatch GHSA-g3hv-75wg-m249 (pattern DoS), via `testcontainers` →
`archiver@7` → `archiver-utils` (glob 10) / `readdir-glob` (minimatch 5). Not
reachable: test-only, and archiver only tars our own `firebase.json`
(testcontainers never calls `glob()`; patterns are ours, no untrusted input).
Fixes (glob 14.0.2 / minimatch 11.0.1, both still CJS with the same `sync` /
`Minimatch` exports) shipped 2026-10-27/28 — too young for `min-release-age=7`.
`audit fix --force` would downgrade testcontainers to 10.x; archiver 8 is
ESM-only, so it can't be overridden in.

**Pay down:** from 2026-11-04: add `"overrides": { "glob": "^14.0.2", "minimatch": "^11.0.1" }` to `e2e/package.json`, `npm install`, confirm `npm audit` is clean + e2e green. Or drop it if testcontainers moves to archiver 8 first.

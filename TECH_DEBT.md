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

### No connection telemetry between item 5 PR A and PR B
**Area:** control-plane / telemetry

PR A deleted the POC sink (`Features/Poc/Telemetry.cs`, raw Npgsql) with the
harness pages that were its only producer. Until PR B adds the tenant-scoped
`connection_sessions` table, real streams log nothing. That breaks the
"connection telemetry is a deliverable" rule. The POC table and its
Checkpoint-1 rows stay in Railway Postgres untouched (code deletion drops
nothing).

**Pay down:** item 5 PR B (next PR), which also drops the POC table once its rows are archived.

### Broker registry is in-memory
**Area:** control-plane / Features/Broker

Broker registry is in-memory: correct only with a single API instance (Railway today).

**Pay down:** second API instance → Postgres LISTEN/NOTIFY or Redis.

### Gateway cert has no rotation
**Area:** gateway / cert rotation

Gateway DTLS cert is 10 years with no rotation (spec non-goal: rotation =
re-onboard + re-pair). A once-a-decade manual step will be forgotten, and Pion
refuses an expired cert → gateway down at expiry. Plan (SSH UpdateHostKeys-style):
~1-year cert; ~60 days before expiry the gateway pre-generates the next cert
(state dir) and sends `{type:"next-pin", fp}` on the `auth` channel *after* the
old pin verified; browser stores it as a second pin; gateway switches at expiry;
reports the new fp to the server (display only). Browsers unused for the whole
window re-pair (= today). ~40 lines Go + ~15 TS + protocol.md message + Go/TS
test. Keep 10 years until this exists.

**Pay down:** item 4 (productized gateway, alongside auto-update).

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

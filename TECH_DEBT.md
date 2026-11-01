# Tech Debt

Deferred hardening, test-harness robustness, and design smells accepted on
purpose. Behavioral defects live in `BUGS.md`. Clear an item by fixing it and
deleting the row (git holds the history).

| # | Area | Description | When to pay it down |
|---|---|---|---|
| T1 | tests / TokenMinter.cs | Emulator error-body parsing assumes an `error` JSON shape; a real HTTP failure (5xx, non-JSON) could be masked. Guard with `TryGetProperty` / `EnsureSuccessStatusCode()`. | Next time the token minter is touched, or if a test fails with a confusing parse error. |
| T3 | tests / AuthReject.Steps | Relies on other step classes' untagged global `[BeforeScenario]` reset for a clean DB; not independent if run alone. | Add its own `[BeforeScenario] ResetAsync()` next time the step file is edited. |
| T4 | control-plane / CORS | POC carries wide-open CORS. Not a hole today (Bearer-header auth, no cookies) but must be scoped before public exposure. | Before the first public/prod deploy. |
| T5 | control-plane / Telemetry.cs | POC connectivity table is raw Npgsql, no EF/migrations, hardcoded — throwaway spike sink. Graduates to a tenant-scoped EF entity when signaling lands. | Item 3/5 (signaling); deleted in that PR. |
| T7 | frontend / npm audit | 4 high `@grpc/grpc-js` advisories (GHSA-m9gg-hp2v-232j, GHSA-f596-whhp-79r4; fixed in ≥1.13.6). Transitive via `firebase` → `@firebase/firestore`, which pins `~1.9.0` even at latest (firebase 13.1.0 / firestore 4.19.0). Not exploitable here: both are gRPC-*server* issues, we never import Firestore, and the built bundle contains no grpc code. No `overrides` forced into a module we don't load; `npm audit fix --force` would downgrade to firebase 9. | When a firebase release moves firestore off grpc-js 1.9 (check at each deps sweep), or immediately if we start using Firestore. |
| T8 | deps / Jellyfin pin | `jellyfin/jellyfin:10.10.7` is ≥2 major lines behind (latest v12.4, 2026-10-26; Jellyfin changed its version scheme after 10.x). The agent drives Jellyfin's API (`agent/cmd/agent/main.go`, `scripts/setup-jellyfin.sh`), so a bump needs the 10.11/12.x API changelog read, agent tunnel tests, and a manual play/seek check — deliberately left out of the 2026-11 deps sweep. | Own `chore/deps-jellyfin` branch; natural fit with the open Jellyfin NAS-footprint + GPLv2 check, or before item 4 (productized agent) hardens the Jellyfin integration. |

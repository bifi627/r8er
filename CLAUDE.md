# r8er

Multi-tenant media platform: users stream their home media (NAS/PC) from a
browser anywhere, with **zero router configuration** — WebRTC data-channel
tunnel to a host agent, TURN fallback. Sensitive files never leave the
user's hardware.

## Read this first

All design lives in `docs/superpowers/specs/` (all dated 2026-07-05).
Authority order when they disagree:

1. `…-r8er-implementation-plan.md` — **phasing, stack, checkpoints. Start here.**
2. `…-r8er-feasibility-design.md` — architecture rationale, risks, privacy rules, cost model
3. `…-r8er-handover-notes.md` — **fine print + what-if hedges. Read before writing tunnel/agent/auth code.**
4. `…-r8er-dev-environment.md` — dev env & testing strategy (this scaffold implements it)

## Current state

**Phase 0 POC complete — Checkpoint 1 passed 2026-07-08 (verdict: GO, proceed
to MVP as designed).** Proven end-to-end: cellular→home connectivity (86%
direct, cellular 100%), TURN fallback, 1080p+subtitles, seek, HEVC transcode
and remux over the tunnel, 4K feasible via ABR. Full record + criteria mapping:
`docs/poc-connectivity-spike.md`. POC code is throwaway (hardcoded room `poc`,
no auth/tenancy) — MVP rewrites it safely. Carried into MVP (non-blocking): iOS
Safari device test, codec census over real libraries, Jellyfin NAS footprint +
GPLv2 check. **Next work: Phase 1 MVP, build order in the implementation plan**
(control plane → agent pairing with fingerprint pinning → tenant-isolated
signaling → productized agent → streaming client → hardening).

**Phase 1 MVP — item 1 (control-plane foundation) complete.** Migration #1
(tenants/users/devices), Firebase ID-token verification (FirebaseAdmin, same path
dev+prod), first-sign-in tenant provisioning, and the ONE EF global query filter
enforcing tenant isolation are in. Integration harness: Testcontainers Postgres +
Firebase Auth emulator, real tokens minted through the prod verify path; behaviors
covered by Reqnroll (isolation/provisioning/auth-reject) + xUnit edges. React:
Firebase sign-in + empty authed shell. **Next: item 2, agent pairing with
DTLS-fingerprint pinning** (the `devices.dtls_fingerprint` / `agent_credential_hash`
columns are the reserved placeholders).

## Stack & layout

| Dir | What | Stack |
|---|---|---|
| `backend/` | Control plane: API + WebRTC signaling (one web app) | .NET 10, EF Core, Postgres |
| `frontend/` | Browser client | React + Vite + TypeScript |
| `agent/` | Host agent: tunnels local Jellyfin over WebRTC | Go + Pion |
| `scripts/` | `gen-test-media.sh` (ffmpeg fixtures) | bash |
| `dev/firebase/` | Auth emulator config for compose | — |

Auth = Firebase (emulator in dev). Media engine = bundled Jellyfin (pinned
image). Hosting = Railway. TURN = Cloudflare (local coturn in dev).

## Commands

```bash
bash scripts/check.sh                   # GOLDEN PATH: all checks (mirrors CI). Run + read output before claiming done.

docker compose up -d                    # deps: postgres, jellyfin, firebase auth emulator
docker compose --profile relay up -d    # + local coturn (force relay via iceTransportPolicy)
bash scripts/gen-test-media.sh          # generate dev-media/ fixtures (needs ffmpeg)

dotnet run --project backend/src/R8er.Api     # backend (or: dotnet watch)
dotnet test backend/R8er.slnx                 # backend tests (Testcontainers → needs Docker)
npm run dev --prefix frontend                 # frontend
npm run build --prefix frontend               # frontend build check
go run ./cmd/agent                            # agent (from agent/)
go test ./...                                 # agent tests (from agent/)
```

Env: copy `.env.example` to `.env`; dev defaults need **no cloud account** —
everything runs locally against emulator/containers.

## Machine notes

- Primary dev machine is Windows 10 + Git Bash + Docker Desktop (WSL2).
  Go 1.26, .NET 10 SDK, Node 24, Docker are present — agent work runs locally
  now (the devcontainer remains for CI parity / clean-room builds).
- `.slnx` (not `.sln`) — .NET 10's solution format.
- `frontend/.npmrc` hardens npm: `ignore-scripts=true`, `min-release-age=7`.
  Keep this in every new package dir (e2e/ etc.). If `npm install` fails
  with `vite@undefined`-style ERESOLVE, a pinned version is younger than
  7 days — relax the semver range, don't remove the hardening.

## Working rules (harness — rationale in `docs/ai-harness-playbook.md`)

- **Ready before code:** a task has acceptance criteria + an explicit out-of-scope list.
- **Done means:** `scripts/check.sh` ran and its output was read; the changed flow was
  actually driven (curl / UI / test output shown); docs invalidated by the change updated
  in the same diff. "It should work" is not evidence.
- **Unit of work:** sized for human review (one reviewable PR), not session capacity.
  No drive-by fixes — unrelated improvements become new tasks.
- **Never satisfy a check by weakening it.** Diffs touching tests, lint config, or CI
  are called out explicitly and get elevated scrutiny.
- **New dependency = stated justification** in the PR/commit body. Lockfile diffs are
  review triggers.
- **Codegen direction (decided):** the WebRTC protocol (signaling messages, tunnel
  framing) is **contract-first** — schema in `docs/protocol.md` once designed, types
  generated/derived for C#, Go, TS. The HTTP API is **code-first** — ASP.NET → OpenAPI →
  generated TS client, once the frontend first consumes it.
- **Rule graduation:** correction → line here → check/codegen. When a rule becomes
  tooling, delete its prose from this file.
- **Deferred on purpose** (rule of two): templates, canonical example files, per-feature
  docs, fine-grained boundary rules. Feature #1 (POC) is built by hand and becomes the
  exemplar at feature #2 — do not scaffold these early.

## Non-negotiable conventions

- **`tenant_id` on every tenant-owned table**, enforced via one EF global
  query filter. Tenant isolation is the product's crown-jewel invariant;
  the signaling isolation tests matter more than any feature.
- **No dev-mode auth bypass, ever.** Same token-verification code path in
  dev and prod; only the issuer differs (Firebase emulator).
- **Never `latest`** for the Jellyfin image; update the pin deliberately.
- **Firebase UID is not a primary key** — own `users.id` (UUID) +
  `firebase_uid` column.
- **CRUD stays boring** (plain EF Core, no repository ceremony). The
  cleverness budget goes to tunnel/signaling/agent only. The owner works
  ponytail-style: laziest solution that works, stdlib first, shortest diff.
- **Connection telemetry is a deliverable**: every WebRTC session attempt
  logs direct-vs-relay, IPv4/v6, throughput to Postgres. Checkpoints read
  this table.
- Data-channel payloads are **chunked (~16–64KB) with backpressure**
  (`bufferedAmount` both ends) — never send whole segments as one message.
- Deploys/publishing are human-approved; no real secrets exist in the repo.

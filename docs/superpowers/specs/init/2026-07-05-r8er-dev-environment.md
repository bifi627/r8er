# r8er — Development Environment & Testing Strategy

**Date:** 2026-07-05
**Status:** Design for the local dev environment, integration testing, and
AI-agent workflow. Companion to the implementation plan; set up during POC,
matured during MVP.

## Principles

1. **Fully local, zero cloud dependencies.** Everything — auth, database,
   media engine, TURN — runs in containers or emulators on the dev machine.
   No Railway, no real Firebase project, no Cloudflare account needed to
   develop or test. This is also what makes the repo workable by AI agents:
   no secrets to provision, nothing to break in shared cloud state.
2. **Tests self-provision.** `dotnet test` / `go test ./...` must pass on a
   machine with only the toolchain + Docker installed. Testcontainers spins
   what a test needs; no "start Postgres first" tribal knowledge.
3. **One command per action.** Start deps, run a component, run tests, seed
   data — each is a single documented command. No orchestration tooling
   (Tilt, Skaffold, k8s) — `docker compose` + native `watch` modes cover it.
4. **The dev environment is the CI environment is the agent environment.**
   Same compose file, same containers, same commands.

## Repo layout (monorepo)

```
r8er/
├── backend/           # .NET 10 solution: API + signaling (one web app)
│   ├── src/R8er.Api/
│   └── tests/R8er.Api.Tests/          # unit + Testcontainers integration
├── frontend/          # React + Vite
├── agent/             # Go host agent (Pion)
│   └── ...            # testcontainers-go integration tests alongside
├── e2e/               # Playwright golden-path test
├── scripts/           # gen-test-media, seed-dev-data (plain scripts)
├── docs/superpowers/specs/            # the design docs
├── docker-compose.yml # dev dependencies (see below)
├── .devcontainer/     # toolchain container for cloud/AI agents
└── CLAUDE.md          # agent onboarding: commands, conventions, doc pointers
```

Monorepo because the interesting bugs live at the seams (client ↔ tunnel ↔
agent ↔ backend); one clone, one CI, atomic cross-component changes.

## Prerequisites (dev machine)

| Tool | Why |
|---|---|
| .NET 10 SDK | backend |
| Node LTS + npm | frontend (npm, no pnpm/yarn — boring wins) |
| Go (current stable) | agent |
| Docker Desktop (WSL2 backend on Windows) | compose deps + Testcontainers |
| ffmpeg | generating test media fixtures (script-invoked) |

Windows note (primary dev machine is Windows 10): Docker Desktop requires
the WSL2 backend; Testcontainers and compose work fine on it. The Go agent
targets Linux containers — develop against Docker, cross-compile via Go's
native tooling, test on real NAS hardware before MVP Checkpoint 3.

## Local runtime: `docker-compose.yml`

| Service | Image | Purpose | Notes |
|---|---|---|---|
| `postgres` | `postgres:17` | control-plane DB | volume-backed; port 5432 |
| `jellyfin` | `jellyfin/jellyfin:<pinned>` | media engine behind the agent | mounts `./dev-media`; **pinned tag, never `latest`** (same discipline as prod) |
| `firebase` | `ghcr.io/…/firebase-emulator` (or `firebase-tools` via npx) | **Auth emulator** | frontend SDK and backend token verification point here in dev |
| `coturn` | `coturn/coturn` | local TURN server | optional profile; used to exercise the relay path deliberately |

Backend, frontend, and agent run **natively** (`dotnet watch run`,
`npm run dev`, `go run ./cmd/agent`) for fast feedback — only the
dependencies live in compose. A `--profile full` variant containerizes
backend+agent too, for the E2E test and for "run the whole thing" demos.

### Firebase emulator specifics

- Frontend: `connectAuthEmulator(auth, 'http://localhost:9099')` when a dev
  flag is set.
- Backend: FirebaseAdmin's verifier doesn't accept emulator tokens out of
  the box — in Development, validate against the emulator's issuer/JWKS (or
  set the SDK's emulator-host env var). **Never a "skip auth in dev" flag**
  — the token path must be the same code path in dev and prod; only the
  issuer differs. Auth bugs hidden by a dev bypass are exactly the bugs this
  product cannot afford.
- No real Firebase project is needed until first deploy.

### Test media fixtures

`scripts/gen-test-media` uses ffmpeg's synthetic sources (`testsrc`,
`sine`) to generate small deterministic clips into `dev-media/` (gitignored):

- `h264-aac.mp4` — the "plays everywhere" baseline
- `hevc-ac3.mkv` — forces Jellyfin to transcode
- `h264-aac-long.mp4` — ~10 min, for seek testing

Generated, not downloaded and not committed: hermetic, license-free, tiny
script, and the matrix grows one ffmpeg line at a time.

## Local WebRTC realities (know these before cursing)

- **Secure context:** WebRTC + Service Workers require HTTPS, except on
  `localhost` — so same-machine dev just works over plain HTTP.
- **Phone-on-LAN testing** needs HTTPS: use `mkcert` + Vite's HTTPS config,
  or a `cloudflared`/Tailscale tunnel to the dev server. Document one path
  in the README; don't build tooling for it.
- **Forcing the relay path:** you cannot simulate CGNAT on a dev machine,
  but you can force TURN with `iceTransportPolicy: 'relay'` in the browser's
  `RTCPeerConnection` config (dev toggle in the client). Pointed at the
  local coturn, this exercises the relay code path without leaving the desk.
- **Real NAT behavior is not locally testable.** That's what the POC
  connectivity spike from real networks is for; don't burn time on network
  namespace simulation rigs.

## Testing strategy

The pyramid, boring on purpose:

### Unit tests
Plain xUnit / `go test` / Vitest. No containers, milliseconds, majority of
tests. Pure logic: signaling message validation, tunnel framing/chunking,
pairing-code generation, EF model rules.

### Integration tests — Testcontainers

**Backend (.NET — `Testcontainers.PostgreSql` + `WebApplicationFactory`):**

- One `PostgreSqlContainer` per test collection (xUnit collection fixture),
  EF migrations applied on startup, **Respawn** resets tables between tests.
  This is the standard pattern; don't invent a variant.
- Auth: a test authentication handler that mints principals directly —
  Firebase stays out of these tests. The Firebase *token verification* code
  itself gets a small dedicated test suite against the emulator container
  (Testcontainers can run it), so the real verifier is covered without
  taxing every test.
- **Signaling/WebSocket tests run here and matter most:**
  `WebApplicationFactory.Server.CreateWebSocketClient()` drives the broker
  in-process. **The tenant-isolation tests are the crown jewels of the whole
  suite** — two tenants, client A attempts to signal tenant B's agent, every
  message type, must be rejected. These are written with the feature (MVP
  step 3) and reviewed at the security checkpoint.

**Agent (Go — `testcontainers-go`):**

- Spin the pinned Jellyfin container with generated fixtures mounted; test
  the agent's proxy layer against the real Jellyfin API (auth injection,
  range-request passthrough, streaming response chunking).
- Tunnel flow-control tests (backpressure, chunk reassembly) run against an
  in-process Pion pair — two `webrtc.PeerConnection`s in one test binary, no
  containers, no browser. This covers the "novel engineering" cheaply.
- Agent ↔ backend signaling contract: test against a stub WebSocket server
  speaking the documented protocol (see contracts, below) — not against the
  real backend; the E2E covers that seam.

### End-to-end — exactly one golden path

Playwright test against `docker compose --profile full up`: sign in
(emulator) → pair agent → library shows fixture media → play `h264-aac.mp4`
→ **seek** → play `hevc-ac3.mkv` (proves transcode path). Headless in CI.

**One** golden path, deliberately. E2E suites rot and flake; edge cases
belong in the integration layer. Add a second E2E only when a real
regression escapes that this one would not have caught.

### CI (GitHub Actions)

Jobs: `backend` (dotnet test — Testcontainers works on standard runners),
`agent` (go test), `frontend` (vitest + lint + build), `e2e` (compose +
Playwright, allowed to be slower). All triggered on PR; the E2E also
nightly. No CD pipeline until MVP nears — Railway deploys can stay manual
that long.

## The AI-agent working environment

What makes this repo good for agentic work (Claude Code local or cloud):

1. **`CLAUDE.md` at the root** — the agent's onboarding: stack summary, the
   one-command actions (run deps, run each component, run each test suite,
   generate fixtures), doc authority order, and a pointer to the handover
   notes. Kept short; it links to docs instead of duplicating them.
2. **`.devcontainer/`** with .NET + Go + Node + ffmpeg + **docker-in-docker**
   (Testcontainers needs a Docker daemon). This gives cloud agents and
   Codespaces the identical environment, and a fresh local checkout works in
   it too.
3. **Hermetic, self-provisioning tests** (principle 2) are the single
   biggest agent enabler: an agent can verify its own work with one command
   per component, no environment archaeology, no flaky shared state.
4. **Contracts as files:** once designed, the signaling message schema and
   the tunnel HTTP framing live in `docs/protocol.md` (plus JSON schema /
   Go+C# types where cheap). The seams between components are exactly where
   parallel agents (one on backend, one on agent, one on frontend) would
   otherwise drift apart — a written contract plus the stub-based contract
   tests keep them honest.
5. **No secrets required for 95% of work** (principle 1). `.env.example`
   documents every variable; real values only exist for deploy tasks, which
   stay human-approved.
6. **Observable by default:** structured logs to stdout, `/healthz` on the
   backend, a `/status` endpoint on the agent (already planned as its local
   status page). An agent verifying a change can curl instead of guessing.
7. **Browser verification:** Playwright is already in the repo for E2E, so
   an agent can drive the real UI to verify frontend changes (screenshot,
   click, play video) — not just run unit tests.
8. **Component independence:** each of backend/agent/frontend builds and
   tests alone. Parallel subagent work maps cleanly onto the directory
   boundaries; only `docs/protocol.md` changes require cross-component
   coordination.

## Deliberately not in the dev environment

- Kubernetes, Tilt, Skaffold, service meshes — compose covers a 4-service
  dev stack.
- A local Railway simulator — the backend is a plain container; Railway is
  a deploy target, not a dev dependency.
- Real Cloudflare TURN in dev — local coturn exercises the relay code path;
  the real thing is validated in the POC connectivity spike and dogfood.
- Mock-heavy test layers between the integration tests and E2E — the
  Testcontainers tests hit real Postgres and real Jellyfin precisely so we
  don't maintain mocks of them.
- Any dev-mode auth bypass (see Firebase emulator section — same code path,
  different issuer, always).

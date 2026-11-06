# StreamIQ — Productized gateway (MVP item 4) — design

Date: 2026-11-04 · Status: draft v2. Pivoted from a Docker updater sidecar to
native binaries plus update notices, after the spike below.

## Intent

Get from "the owner runs `go run ./cmd/gateway` with hand-made env" to "a
household downloads one binary, runs one command, and is told when to
update". The gateway runs **natively** on Windows, Linux and macOS (amd64 +
arm64). Jellyfin comes either from its own installer or from an optional StreamIQ
compose file that pins a tested version.

The owner's dev machine must run the dev stack and one or more installed
copies side by side.

## Deviation from the implementation plan

Plan item 4 says "**auto-update from day one**". This design ships
**notify-only** updates: the gateway detects a new release and tells the user
the one command to run. Accepted for dogfood (owner + 1–2 friends). It is the
first thing to revisit before strangers install. Upgrade path: a scheduled
task / systemd timer runs the same install script (no new mechanism).
Recorded in the plan with a pointer here.

## Goals (acceptance criteria)

1. **Releases:** tag `gateway-vX.Y.Z` → CI builds `streamiq-gateway-<os>-<arch>`
   for windows/linux/darwin × amd64/arm64 (`go build`, version via
   `-ldflags -X main.version`), plus `SHA256SUMS`, `install.sh`, `install.ps1`
   and `jellyfin-compose.yml`, published as a GitHub Release with `gh release
   create`. The tag push is the human approval. No new tools.
2. **Install = update:** `install.ps1` / `install.sh` download the platform
   binary from `releases/latest`, verify it against `SHA256SUMS`, put it in the
   install dir, and (re)start it. First run takes the enrollment token from the
   web app's Add gateway command. Later runs never touch the state dir, so
   pairings survive updates.
3. **Update notice:** at start and then daily, the gateway calls `GET
   api.github.com/repos/bifi627/streamiq/releases/latest`. If that tag is newer
   than its own version, it logs `update available vA → vB` plus the install
   command for its OS, and the status page shows the same. Failures are
   logged once and ignored. A `dev` build skips the check.
4. **Jellyfin key without manual steps:**
   - Fresh Jellyfin (setup wizard not done, e.g. the StreamIQ compose): the gateway
     completes the wizard, creates the admin user (random password, kept in
     the state dir and shown on the local page) and mints its API key.
   - Jellyfin already set up: the local page asks once for a Jellyfin admin
     login; the gateway mints a key with it and stores the key, not the
     password.
   - `STREAMIQ_JELLYFIN_API_KEY` still wins (dev, e2e).
5. **Optional Jellyfin compose** (`jellyfin-compose.yml`, release asset): just
   Jellyfin, pinned to the version this release was tested with, port 8096,
   named config/cache volumes, media folders as long-syntax binds (the user
   edits this file; nothing overwrites it). The gateway reads Jellyfin's
   version from `/System/Info/Public` and warns (log + status page) when it
   differs from the tested pin baked into the binary.
6. **Version visible:** heartbeat carries the gateway version; the Gateways
   list shows it (migration #5).
7. **Status page** (local page): version and update notice, API connected +
   last heartbeat, Jellyfin reachable + version vs tested, Jellyfin admin
   credentials when the gateway created them.
8. **Gateway name** defaults to the OS hostname (as today natively); the
   install command can pass `STREAMIQ_NAME`.
9. **Structured logs:** `log/slog` JSON (stdlib).
10. Reconnect after a gateway restart/update is exercised (Checkpoint 2).

## Non-goals

Automatic updates (see deviation); running the gateway as a Windows
service / systemd unit (the installer starts it; autostart is a documented
manual step for now); bundling Jellyfin; code signing for Windows
SmartScreen / macOS Gatekeeper (users see the "unknown publisher" prompt);
media management UI (IDEAS.md "Media management"); cert rotation
(TECH_DEBT.md, own PR); Docker as the supported gateway runtime (kept as a
tested option, below).

## Decisions

### Native gateway, not a container (spike result)
Docker Desktop's userspace networking gives each destination a different
public port (probe: one socket → three STUN servers → 64475 / 64475 /
64506; native Windows: 64302 ×3). The srflx candidate is useless, so a
containerized gateway on Docker Desktop was **relayed on cellular**, while
the native gateway connects directly. Linux Docker uses kernel NAT and should
be fine, but this is unverified. So the native binary is the default.
`gateway/Dockerfile` + `STREAMIQ_ICE_PORT` (one published UDP port, host LAN IP
advertised) stay as the tested Linux-Docker option, exercised by
`E2E_GATEWAY_IMAGE=streamiq-gateway:dev npm test`. Docker has no IPv6 by default,
which is one more reason to stay native.

### Install layout
```
Windows: %LOCALAPPDATA%\StreamIQ\gateway.exe, state\, gateway.env
Linux/macOS: ~/.local/share/streamiq/gateway, state/, gateway.env
```
`gateway.env` holds what the user configured (`STREAMIQ_API_URL`, `STREAMIQ_NAME`,
ports). The installer writes it on first run and leaves it alone afterwards.
`STREAMIQ_ENROLL_TOKEN` is passed only on the first run, never stored. The
gateway reads `gateway.env` itself (a few lines of stdlib parsing), so it
can be started by hand or by a scheduled task without a wrapper.

### Release check details
Compare versions as semver on `gateway-v` tags. Releases marked prerelease
are skipped by GitHub's `latest`, which makes a nightly channel later just
"follow prereleases" (IDEAS.md "Nightly channel"). Unauthenticated limit is
60 requests/hour/IP; one call a day is far below it.

### Integrity
The installer refuses a binary whose SHA256 doesn't match `SHA256SUMS` from
the same release. That catches corrupted downloads, not a compromised GitHub
account. Signing (cosign / minisign) belongs with the strangers-install
work, same as the auto-update upgrade.

## Dev machine: dev stack + installed copies

| | Dev stack | Installed copy |
|---|---|---|
| Gateway | `go run` from `gateway/` | released binary in the install dir |
| State | `gateway/streamiq-state` | `<install dir>/state` |
| API / auth | `localhost:5028`, emulator | prod (or Railway dev), real Firebase |
| Browser origin | `localhost:5173` | prod URL, so separate client keys and pins |
| Jellyfin | dev compose (`-p streamiq`) | `jellyfin-compose.yml` (own project) or a native install |
| Ports | **moved to 18096 / 18097** | product defaults 8096 / 8097 |

The dev side moves ports so an installed copy is a genuinely default install:
dev `docker-compose.yml` maps `18096:8096`; `.env.example`,
`setup-jellyfin.sh` and the dev gateway run command use
`STREAMIQ_ORIGIN=http://localhost:18096` and `STREAMIQ_PAGE_PORT=18097`. Binary defaults
stay 8096/8097. e2e uses random ports and is unaffected. A second installed
copy (e.g. nightly later) = another install dir with its own state, page port
and Jellyfin.

## PR split

- **PR A — release + install:** release workflow, `install.sh`/`install.ps1`,
  `gateway.env`, update notice, version in heartbeat + Gateways list, slog,
  dev port move, docs (`tools.md`, CLAUDE.md, install guide).
- **PR B — Jellyfin without manual steps:** wizard bootstrap, admin-login key
  mint on the local page, `jellyfin-compose.yml`, tested-version warning,
  status page.
- **Then:** cert rotation (TECH_DEBT.md); auto-update + signing before
  strangers.

## Spike record (2026-11-04, Windows 10 + Docker Desktop 29.9)

- Container gateway, LAN: direct. e2e golden path passes against the image.
- Container gateway, cellular: **relayed**. Cause: Docker Desktop NAT is
  destination-dependent (probe above). Native: direct.
- Windows paths in compose long syntax (`D:\Movies`) work even when compose
  runs inside a Linux container.
- Compose run from a container doesn't recreate host-started containers;
  image changes recreate them with volumes and binds intact. (Only relevant
  if the sidecar idea returns.)
- Gateway name inside a container = container ID; native uses the hostname.

## Resolved

1. GitHub repo and release assets are public.
2. Media folders are configured in Jellyfin (native install) or as binds in
   `jellyfin-compose.yml`, across any number of drives.
3. Jellyfin admin password on the LAN page: OK for now; TECH_DEBT.md "Local
   page shows Jellyfin admin password unauthenticated".
4. Nightly: IDEAS.md "Nightly channel" (now = prereleases).

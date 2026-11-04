# r8er — Productized gateway (MVP item 4) — design

Date: 2026-11-04 · Status: draft (sidecar-updater approach approved; details open)

## Intent

Get from "the owner runs `go run ./cmd/gateway` with hand-made env" to "a
household runs one command and has a gateway + Jellyfin that stays current
by itself". Linux Docker and Windows Docker Desktop, amd64 + arm64. Other
platforms (Synology/unRAID packages, native Windows/Mac) on demand.

The owner's dev machine must be able to run **both**: a normal install
(talking to prod, used daily) and the full dev stack, side by side, without
either touching the other's state.

## Goals (acceptance criteria)

1. **Image:** `ghcr.io/bifi627/r8er-gateway:<version>`, multi-arch, built by
   CI from `gateway/`. Version baked in via `-ldflags`. Publishing is a
   human-approved workflow run.
2. **Install:** the web app's Add gateway shows one command (Linux `sh`,
   Windows PowerShell) that creates an `r8er/` folder, writes `.env`, fetches
   `compose.yml` from the API, and runs `docker compose up -d`. Result: an
   enrolled gateway whose local page shows the comparison code.
3. **No manual Jellyfin setup:** on first run the gateway completes the
   Jellyfin startup wizard, creates the admin user (random password, kept in
   the state dir, shown on the local page) and mints its API key. Libraries
   are the user's call, in Jellyfin. `R8ER_JELLYFIN_API_KEY` still wins when set (dev, e2e).
4. **Auto-update:** an updater sidecar applies a new published `compose.yml`
   within 24h with no user action (see "Update lifecycle").
5. **Version visible:** heartbeat carries the gateway version; the Gateways
   list shows it (rollout progress at a glance).
6. **Status page** (local page, `:8097`): version, API connected + last
   heartbeat, Jellyfin reachable, last update check/result, Jellyfin admin
   credentials, and a loud warning when no hardware transcode device is
   present.
7. **Structured logs:** `log/slog` JSON handler (stdlib).
8. **Dev/install separation** holds (section below), proven by running both
   on the owner's Windows machine at once.
9. Reconnect after a gateway restart/update is exercised (Checkpoint 2).

## Non-goals

Image/compose signing (before strangers install — item 6 / Checkpoint 3);
updater self-update (re-run install); Jellyfin downgrade/rollback; media
management UI (IDEAS.md "Media management"); native packages; staged
rollouts / channels; update-time-avoids-active-stream logic (fixed 04:00
window instead); cert rotation (TECH_DEBT.md — own PR after this).

## Decisions

### One published `compose.yml` = one release
`deploy/compose.yml` in the repo pins the **exact** gateway image version and
the **exact** Jellyfin version (never `latest`, never a moving tag). The API
image copies `deploy/` into `wwwroot/install/`, so prod serves
`/install/compose.yml`, `/install/install.sh`, `/install/install.ps1`. Releasing
= bump the pins, merge, approve the API deploy. Rollback = deploy the previous
`compose.yml`. The deploy you already approve by hand is the release gate.

### Files on the user's machine
```
r8er/
  compose.yml            ours — the updater overwrites it
  .env                   the user's — never touched by the updater
  compose.override.yml   the user's — media folders (see "Media folders")
```
Everything user-specific lives in `.env` / the override: `R8ER_API_URL`,
`R8ER_ENROLL_TOKEN` (dead after first enroll), `R8ER_LAN_URL`, `TZ`, optional
host-port overrides, optional `R8ER_UPDATE_URL` (channel, see below),
optional `COMPOSE_PROFILES=hwaccel` (adds the
`/dev/dri` device to Jellyfin). That split is what makes overwriting
`compose.yml` safe.

`compose.yml` has top-level `name: r8er-home` and three services:

| Service | Image | Notes |
|---|---|---|
| `gateway` | `${R8ER_GATEWAY_IMAGE:-ghcr.io/bifi627/r8er-gateway:X.Y.Z}` | unprivileged; named volume `state`; `R8ER_ORIGIN=http://jellyfin:8096`; page `${R8ER_PAGE_PORT:-8097}:8097`, ICE `${R8ER_ICE_PORT:-8098}:8098/udp` |
| `jellyfin` | `jellyfin/jellyfin:<pinned>` | named volumes `config`/`cache`; media mounts come from the override; dashboard `${R8ER_JELLYFIN_PORT:-8096}:8096` |
| `updater` | `docker:<pinned>-cli` | Docker socket; mounts `r8er/` at `/r8er`; runs `deploy/update.sh` |

No relative bind mounts: compose runs inside the updater container, so
relative paths would resolve against the container's filesystem, not the
host's. Named volumes + absolute media paths only.

### Media folders (any number of drives)
Jellyfin only sees host folders mounted into its container, so each media
folder is one mount under `/media/<name>`. They live in
`compose.override.yml`, which Compose merges with `compose.yml` automatically
(default file names) and the updater never touches:
```yaml
services:
  jellyfin:
    volumes:
      - { type: bind, source: /mnt/disk1/Movies, target: /media/movies, read_only: true }
      - { type: bind, source: /mnt/disk2/Series, target: /media/series, read_only: true }
```
- Installer: `-Media <dir>[,<dir>…]` (optional) writes the override, target =
  folder name. No arg → one `<install dir>/media` folder.
- Adding a drive later: one line in the override + `docker compose up -d`, then
  add the library in Jellyfin. (A local-page "Add folder" that edits the
  override = IDEAS.md "Media management".)
- Libraries are created **in Jellyfin** by the user; the bootstrap does not add
  any (it can't know which folder is movies vs series).
- Long syntax on purpose: the short `D:\Movies:/media/movies` form splits on
  the drive-letter colon.
- **Windows path spike:** compose runs inside the (Linux) updater container,
  where `D:\Movies` is not an absolute path. The installer likely has to write
  Docker Desktop's host form (`/run/desktop/mnt/host/d/Movies`). Verify in the
  ICE spike, alongside "updater recreates Jellyfin with its mounts intact".
- Updater validation (step 3) checks the **merged** result:
  `docker compose -f compose.yml.new -f compose.override.yml config -q`.

### Update lifecycle (`deploy/update.sh`, ~15 lines)
At start, then daily at 04:00 `TZ` + random 0–30 min:
1. `curl -fsS ${R8ER_UPDATE_URL:-$R8ER_API_URL/install/compose.yml}` → temp file. Fail → keep, retry tomorrow.
2. Unchanged → done.
3. `docker compose config -q` on it. Invalid → discard.
4. Swap in; note current image IDs of `gateway`/`jellyfin`;
   `docker compose pull --ignore-pull-failures gateway jellyfin`;
   `docker compose up -d gateway jellyfin` (only changed containers recreate;
   a missing image fails `up` before the old container is removed).
5. `docker rmi` the noted old image IDs (errors ignored). **Never**
   `docker image prune` — on a shared daemon (the dev machine) that reaches
   beyond this project.
6. Write the result to a status file in the gateway's state volume (last check
   + outcome) → status page.

"Update now" = `docker restart r8er-home-updater-1`. The updater never
recreates itself: compose would kill its own process mid-run. Its image
changes rarely; a new one = re-run the install command.

**Failure modes:** bad gateway release crash-loops → publish the previous
`compose.yml`; the independent updater rolls the fleet back. Jellyfin bumps
are **one-way** (it migrates its DB, no downgrade) → test locally first. API
down → no change. **Compromised API = root on every host** (the updater holds
the Docker socket): accepted for dogfood only; signing closes it before
strangers.

### WebRTC from inside a container
In a bridge network the gateway's host candidates are container IPs (useless
on the LAN). Fix: gateway listens for ICE on one UDP port (Pion
`SettingEngine.SetICEUDPMux`, `R8ER_ICE_PORT`), compose publishes it, and the
gateway advertises the host's LAN IP from `R8ER_LAN_URL`
(`SetNAT1To1IPs(…, ICECandidateTypeHost)`). Both unset (native dev) =
today's behavior. srflx keeps working through Docker's NAT. **Spike first**:
confirm direct (not relay) on LAN and from cellular, on Linux and on Docker
Desktop — the `connection_sessions` telemetry tells us. If Docker Desktop
relays everything, Windows falls back to the native `gateway.exe` + a
scheduled task running the same download-and-restart.

### Jellyfin bootstrap (gateway, `internal/jellyfinsetup`)
Port of `scripts/setup-jellyfin.sh` steps 3–4 + key mint (no library step) to Go, run once when
the state dir has no Jellyfin key and Jellyfin reports the wizard incomplete.
If the wizard is already done (user set Jellyfin up themselves) and no key is
configured → status page says "create an API key and set R8ER_JELLYFIN_API_KEY"
(today's behavior). Key + admin password stored in the state dir (0600).

## Dev machine: installed vs dev, side by side

Both stacks share one Docker Desktop daemon. They're kept apart along every
axis:

| Axis | Dev stack | Installed (prod) |
|---|---|---|
| Compose project | `r8er` (`docker compose -p r8er`, repo root) | `r8er-home` (`name:` in `compose.yml`, own folder e.g. `%USERPROFILE%\r8er`) |
| Containers / volumes / network | `r8er-*` | `r8er-home-*` |
| Gateway | native `go run` | container |
| Gateway state | `gateway/r8er-state` | named volume `r8er-home_state` |
| API | `localhost:5028` + dev DB | prod Railway |
| Auth | Firebase emulator | real Firebase |
| Browser client key | origin `localhost:5173` | prod origin — separate IndexedDB, separate pins |
| Host ports | **moved to 18096 (Jellyfin), 18097 (gateway page)** | product defaults 8096, 8097, 8098/udp |

Port collision is the only real conflict. The dev side moves, so the
installed side is a genuinely normal install with defaults: dev
`docker-compose.yml` maps `18096:8096`; `.env.example`, `dev.ps1` /
`setup-jellyfin.sh` and the gateway run command use `R8ER_ORIGIN=http://localhost:18096`
and `R8ER_PAGE_PORT=18097`. (Binary defaults stay 8096/8097 — they are the
product's.) e2e uses Testcontainers random ports — unaffected.

**Testing item 4 itself (third, optional stack):** to try install/update
against the *dev* API without publishing anything:
```
docker build -t r8er-gateway:dev gateway
install.ps1 -Api http://host.docker.internal:5028 -Dir $HOME\r8er-dev
# .env: COMPOSE_PROJECT_NAME=r8er-home-dev, R8ER_GATEWAY_IMAGE=r8er-gateway:dev,
#       R8ER_PAGE_PORT=28097, R8ER_ICE_PORT=28098, R8ER_JELLYFIN_PORT=28096
```
`COMPOSE_PROJECT_NAME` in `.env` outranks `name:` (Compose precedence), so it
can't collide with `r8er-home`. The dev API serves the repo's `deploy/` the
same way prod does. Pull failures for the local-only image are ignored by the
updater (step 4), so the update loop is exercised end to end.

**Landmines (go in `docs/tools.md`):** never `docker volume prune` /
`docker system prune --volumes` on the dev machine. They delete the
installed gateway's identity (all browsers must re-pair) and its Jellyfin
config. `scripts/setup-jellyfin.sh` only touches `r8er_*` volumes. Keep it
that way.

### Channels / nightly (not built now; must stay possible)
A channel is just **which `compose.yml` the updater fetches**. Nothing in
this design assumes a single one:
- **Nightly gateway against prod API:** CI publishes `r8er-gateway:nightly-<sha>`
  and `deploy/nightly/compose.yml` (served at `/install/nightly/compose.yml`);
  the install sets `R8ER_UPDATE_URL` to it. Only needs the one `.env` knob,
  which the updater reads from day one.
- **Nightly whole app (API + SPA + gateway):** a second Railway environment.
  Install with `-Api <nightly-url>`; its API serves its own `compose.yml`.
  Zero extra code.

A nightly copy on the dev machine is a fourth stack, same recipe as the
item-4 test stack (`COMPOSE_PROJECT_NAME=r8er-home-nightly`, own ports). It
has its **own Jellyfin config volume**, so a nightly Jellyfin bump (one-way
migration) never touches the stable copy's library. Nightly is where Jellyfin
bumps get tested before stable. Both can mount the same media folder `:ro`.
Protocol rule this implies: a stable gateway must keep working against
whatever API it talks to (API changes stay backward-compatible with the
oldest gateway still in the field).

## PR split

- **PR A — install it:** gateway Dockerfile + CI image build (publish job
  manual), `deploy/compose.yml` + `install.sh`/`install.ps1` served from the
  API, Jellyfin bootstrap, ICE UDP mux + NAT1To1 (after the spike), slog,
  version in heartbeat + Gateways list (migration #5), status page, dev port
  move, docs (`tools.md`, CLAUDE.md, install guide).
- **PR B — keep it current:** updater service + `update.sh`, status file →
  status page, one real update shipped to the owner's install (Checkpoint 3
  evidence starts here).
- **Then:** cert rotation (TECH_DEBT.md), signing (before strangers).

## Resolved (2026-11-04)

1. **GHCR image public:** yes (the repo is public anyway).
2. **Media folder at install:** optional; any number of folders across
   drives via `compose.override.yml` (see "Media folders").
3. **Admin password on the LAN page:** OK for now; TECH_DEBT.md "Local page
   shows Jellyfin admin password unauthenticated".
4. **Nightly channel:** not built; IDEAS.md "Nightly channel". The
   `R8ER_UPDATE_URL` knob ships now so it stays possible.

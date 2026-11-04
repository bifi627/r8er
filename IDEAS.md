# Ideas

Wanted but not yet built, and neither a defect (`BUGS.md`) nor debt
(`TECH_DEBT.md`). Promote to a task when picked up; delete when done or
dropped. No numbers — reference items by heading.

### Phone as a local dev client (LAN + HTTPS)
**Area:** dev env

Local dev only works from the PC itself: Vite + backend bind `localhost`, the
page hardcodes `http://localhost:5028` (API) and `:9099` (auth emulator), and
plain `http://<pc-ip>` is not a secure context → no `crypto.subtle` → no client
key → a phone can't pair. Owner wants the phone as second local client. Plan:
Vite `--host` + self-signed HTTPS (`@vitejs/plugin-basic-ssl`, new dev dep —
justify), backend on `0.0.0.0` with HTTPS (or proxied through Vite so the phone
needs one cert), API/emulator URLs derived from `location.hostname`, `dev.ps1`
switch (e.g. `-Lan`). Phone clicks through one cert warning. Gateway page
(`http://<pc-ip>:8097`) stays plain HTTP — needs no WebCrypto. Interim: test
phones against the Railway dev deployment (HTTPS).

**When:** before the iOS device test / item 5 streaming client, whichever comes first.

### Media management (library folders, rescan)
**Area:** gateway / Jellyfin admin

Item 5 is consumption-only (browse, play, seek, resume). Nothing in the plan
owns Jellyfin setup (admin user, API key, library folders) or upkeep (add a
folder, rescan, fix metadata) — implicitly item 4's job, which is deferred.
Today: owner sets Jellyfin up once by hand in its own dashboard
(`http://localhost:8096`; dev: `scripts/setup-jellyfin.sh`). Plan: small admin section on the LAN-only gateway
page (`:8097`) that drives Jellyfin's API — folder picker + rescan; stays
local, so nothing sensitive crosses the control plane. Not in the r8er web app
(remote admin = new attack surface).

**When:** with item 4 (productized gateway), or once a non-owner household installs.

### Nightly channel
**Area:** gateway / deploy

Prerelease updates for the owner's second installed copy. A channel = which
`compose.yml` the updater fetches (`R8ER_UPDATE_URL`, shipped with item 4).
Gateway-only nightly: CI publishes `r8er-gateway:nightly-<sha>` +
`deploy/nightly/compose.yml` (served at `/install/nightly/compose.yml`).
Whole-app nightly: a second Railway environment, install with `-Api <url>`.
On the dev machine: own `COMPOSE_PROJECT_NAME` + ports, own Jellyfin config
volume (nightly tests one-way Jellyfin bumps before stable). Detail: item 4
spec, "Channels / nightly".

**When:** once there are real users on stable worth protecting from untested builds.

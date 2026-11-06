# Installing the StreamIQ gateway

The gateway is one native binary on the machine that has your media (NAS, PC,
Mac). It needs a running Jellyfin on the same machine (default
`http://localhost:8096`): either your own [Jellyfin install](https://jellyfin.org/downloads)
or the optional pinned compose file below.

## Install

1. In the StreamIQ web app: **Gateways → Add gateway**. Copy the command for your
   shell. It contains a one-time token that is valid for 15 minutes.
2. Run it on the media machine:
   - Linux / macOS: `curl -fsSL …/install.sh | STREAMIQ_API_URL=… STREAMIQ_ENROLL_TOKEN=… sh`
   - Windows (PowerShell): `$env:STREAMIQ_API_URL='…'; $env:STREAMIQ_ENROLL_TOKEN='…'; irm …/install.ps1 | iex`
3. Open the local page it prints (`http://localhost:8097`). On Windows, allow
   the firewall prompt for private networks.
4. Connect Jellyfin. The page's status section shows what it needs:
   - **Fresh Jellyfin** (setup wizard not done): nothing to do. The gateway
     completes the wizard, creates the admin `streamiq` with a random password
     and shows that password on the local page. Add your media libraries in
     Jellyfin's dashboard with it.
   - **Jellyfin already set up:** sign in once on the local page with a Jellyfin
     administrator. The gateway creates an API key named `streamiq-gateway` and
     keeps only the key (`state/jellyfin.json`), not the password. Playback
     uses that user's library and watch progress (`STREAMIQ_JELLYFIN_USER` picks
     another user).
   - `STREAMIQ_JELLYFIN_API_KEY` in `gateway.env` overrides both.

## Optional: Jellyfin via Docker

Each release ships `jellyfin-compose.yml`: just Jellyfin, pinned to the
version the gateway was tested with, on port 8096. Download it next to the
gateway, edit the media folders (one read-only bind per folder; Windows paths
like `'D:\Movies'` work), then run `docker compose -f jellyfin-compose.yml up -d`
and leave the setup wizard to the gateway. Updates never overwrite this file.
The status page warns when Jellyfin runs a version the gateway wasn't tested with.

The installer verifies the binary against the release's `SHA256SUMS` and
refuses a mismatch.

| | Windows | Linux / macOS |
|---|---|---|
| Install dir | `%LOCALAPPDATA%\streamiq` | `~/.local/share/streamiq` |
| Settings | `gateway.env` (yours, kept on updates) | same |
| Identity + paired browsers | `state\` (never touched by updates) | `state/` |
| Log | `gateway.log` | `gateway.log` |

`STREAMIQ_DIR` overrides the install dir. Set `STREAMIQ_NAME` and `STREAMIQ_PAGE_PORT` on
the first install to change the gateway's name or page port.

## Update

The gateway checks GitHub once a day. When a newer release exists, it logs
`update available vA → vB. Run: …` and shows the same on its local page. Run
that command:

- Linux / macOS: `curl -fsSL https://github.com/bifi627/streamiq/releases/latest/download/install.sh | sh`
- Windows: `irm https://github.com/bifi627/streamiq/releases/latest/download/install.ps1 | iex`

It stops the running gateway, swaps the binary and starts it again. Your
settings and paired browsers stay. Nothing updates without you running it
(notify-only for now).

## Start on boot (manual for now)

- Windows: `schtasks /Create /SC ONLOGON /TN streamiq-gateway /TR "'%LOCALAPPDATA%\StreamIQ\gateway.exe'"`
- Linux / macOS: `crontab -e`, add `@reboot cd ~/.local/share/streamiq && ./gateway >> gateway.log 2>&1`

## Releasing (maintainers)

Push a tag `gateway-vX.Y.Z`. CI (`release-gateway.yml`) tests, builds all six
binaries, and publishes the GitHub Release with `SHA256SUMS`, both install
scripts and `jellyfin-compose.yml`. When bumping the Jellyfin pin, update
`docker-compose.yml`, `gateway/install/jellyfin-compose.yml` and
`jellyfin.Tested` together (a gateway test enforces it). A tag with a suffix (`gateway-v1.5.0-rc.1`) becomes a prerelease,
which `latest` and the update check skip.

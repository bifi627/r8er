# Installing the r8er gateway

The gateway is one native binary on the machine that has your media (NAS, PC,
Mac). It needs a running [Jellyfin](https://jellyfin.org/downloads) on the
same machine (default `http://localhost:8096`).

## Install

1. In the r8er web app: **Gateways → Add gateway**. Copy the command for your
   shell. It contains a one-time token that is valid for 15 minutes.
2. Run it on the media machine:
   - Linux / macOS: `curl -fsSL …/install.sh | R8ER_API_URL=… R8ER_ENROLL_TOKEN=… sh`
   - Windows (PowerShell): `$env:R8ER_API_URL='…'; $env:R8ER_ENROLL_TOKEN='…'; irm …/install.ps1 | iex`
3. Open the local page it prints (`http://localhost:8097`). On Windows, allow
   the firewall prompt for private networks.
4. Until item 4 PR B: give the gateway a Jellyfin API key. In the Jellyfin
   dashboard, go to API Keys and create one, then add
   `R8ER_JELLYFIN_API_KEY=<key>` to `gateway.env` and re-run the update command.

The installer verifies the binary against the release's `SHA256SUMS` and
refuses a mismatch.

| | Windows | Linux / macOS |
|---|---|---|
| Install dir | `%LOCALAPPDATA%\r8er` | `~/.local/share/r8er` |
| Settings | `gateway.env` (yours, kept on updates) | same |
| Identity + paired browsers | `state\` (never touched by updates) | `state/` |
| Log | `gateway.log` | `gateway.log` |

`R8ER_DIR` overrides the install dir. Set `R8ER_NAME` and `R8ER_PAGE_PORT` on
the first install to change the gateway's name or page port.

## Update

The gateway checks GitHub once a day. When a newer release exists, it logs
`update available vA → vB. Run: …` and shows the same on its local page. Run
that command:

- Linux / macOS: `curl -fsSL https://github.com/bifi627/r8er/releases/latest/download/install.sh | sh`
- Windows: `irm https://github.com/bifi627/r8er/releases/latest/download/install.ps1 | iex`

It stops the running gateway, swaps the binary and starts it again. Your
settings and paired browsers stay. Nothing updates without you running it
(notify-only for now).

## Start on boot (manual for now)

- Windows: `schtasks /Create /SC ONLOGON /TN r8er-gateway /TR "'%LOCALAPPDATA%\r8er\gateway.exe'"`
- Linux / macOS: `crontab -e`, add `@reboot cd ~/.local/share/r8er && ./gateway >> gateway.log 2>&1`

## Releasing (maintainers)

Push a tag `gateway-vX.Y.Z`. CI (`release-gateway.yml`) tests, builds all six
binaries, and publishes the GitHub Release with `SHA256SUMS` and both install
scripts. A tag with a suffix (`gateway-v1.5.0-rc.1`) becomes a prerelease,
which `latest` and the update check skip.

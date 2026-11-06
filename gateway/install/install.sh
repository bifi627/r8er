#!/bin/sh
# r8er gateway: install, or update in place (item 4). The web app's
# Gateways -> Add gateway shows the first-run command (it sets R8ER_API_URL +
# R8ER_ENROLL_TOKEN); later, updating is just:
#   curl -fsSL https://github.com/bifi627/r8er/releases/latest/download/install.sh | sh
# The state dir (cert, credential, paired browsers) is never touched.
set -eu
DIR="${R8ER_DIR:-$HOME/.local/share/r8er}"
DL="${R8ER_DOWNLOAD:-https://github.com/bifi627/r8er/releases/latest/download}"

os=$(uname -s | tr '[:upper:]' '[:lower:]')
case "$(uname -m)" in
  x86_64 | amd64) arch=amd64 ;;
  aarch64 | arm64) arch=arm64 ;;
  *) echo "r8er: unsupported CPU $(uname -m)" >&2; exit 1 ;;
esac
bin="r8er-gateway-$os-$arch"

if [ ! -f "$DIR/gateway.env" ] && [ -z "${R8ER_API_URL:-}" ]; then
  echo "r8er: first install needs the command from the web app (Gateways -> Add gateway)" >&2
  exit 1
fi

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
curl -fsSL -o "$tmp/$bin" "$DL/$bin"
curl -fsSL -o "$tmp/SHA256SUMS" "$DL/SHA256SUMS"
if command -v sha256sum >/dev/null 2>&1; then sum="sha256sum"; else sum="shasum -a 256"; fi
(cd "$tmp" && grep "[ *]$bin$" SHA256SUMS | $sum -c - >/dev/null) || {
  echo "r8er: checksum mismatch for $bin, not installing" >&2
  exit 1
}

mkdir -p "$DIR/state"
pkill -f "^$DIR/gateway$" 2>/dev/null && sleep 1 || true # running copy (update)
chmod +x "$tmp/$bin"
mv "$tmp/$bin" "$DIR/gateway"

if [ ! -f "$DIR/gateway.env" ]; then
  {
    echo "R8ER_API_URL=$R8ER_API_URL"
    echo "R8ER_STATE_DIR=$DIR/state"
    # Optional first-install settings persist, so updates keep them.
    [ -n "${R8ER_NAME:-}" ] && echo "R8ER_NAME=$R8ER_NAME"
    [ -n "${R8ER_PAGE_PORT:-}" ] && echo "R8ER_PAGE_PORT=$R8ER_PAGE_PORT"
    true
  } > "$DIR/gateway.env"
fi

# The enrollment token only matters on the first start; it is never stored.
# Full path on purpose: pkill above matches it on the next update.
cd "$DIR" && nohup "$DIR/gateway" >> "$DIR/gateway.log" 2>&1 &
sleep 2
echo "r8er gateway started"
port=$(sed -n 's/^R8ER_PAGE_PORT=//p' "$DIR/gateway.env")
echo "  local page: http://localhost:${port:-8097}"
echo "  log:        $DIR/gateway.log"
echo "  autostart (not set up for you): crontab -e, add"
echo "    @reboot cd $DIR && $DIR/gateway >> $DIR/gateway.log 2>&1"

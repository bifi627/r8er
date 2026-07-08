#!/usr/bin/env bash
# Reproducible dev Jellyfin setup for POC step 5 (playback through the tunnel).
# Wipes the throwaway Jellyfin config, re-runs the startup wizard via the REST
# API with known dev creds, adds the dev-media library, scans it, mints an API
# key, and prints a ready-to-play HLS URL for the 10-minute seek clip.
#
# ponytail: throwaway dev tooling — hardcoded creds, no error recovery beyond
# "fail loud". Not product code; the agent owns the real Jellyfin key in MVP.
#
#   bash scripts/setup-jellyfin.sh
#
# Requires: docker compose (jellyfin service), curl, node (JSON parsing).
set -euo pipefail

BASE="http://localhost:8096"
USER_NAME="r8er"
USER_PW="${JELLYFIN_DEV_PW:-r8erdev}"
AUTH_HDR='MediaBrowser Client="r8er-setup", Device="setup", DeviceId="r8er-setup", Version="0.0.1"'

# node -e helper: read stdin JSON, print the value at a dotted/indexed path.
jget() { node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const o=JSON.parse(s);let v=o;for(const k of process.argv[1].split("."))v=Array.isArray(v)?v[+k]:v?.[k];process.stdout.write(String(v??""))})' "$1"; }

echo "== 1/7 wiping Jellyfin config volume (fresh wizard) =="
docker compose stop jellyfin >/dev/null 2>&1 || true
docker compose rm -f jellyfin  >/dev/null 2>&1 || true
docker volume rm r8er_jellyfin-config r8er_jellyfin-cache >/dev/null 2>&1 || true
docker compose up -d jellyfin >/dev/null

echo "== 2/7 waiting for Jellyfin to come up =="
for i in $(seq 1 60); do
  if curl -fsS "$BASE/System/Info/Public" >/dev/null 2>&1; then break; fi
  sleep 1
  [ "$i" = 60 ] && { echo "jellyfin did not start"; exit 1; }
done

echo "== 3/7 completing startup wizard (admin: $USER_NAME) =="
curl -fsS -X POST "$BASE/Startup/Configuration" -H 'Content-Type: application/json' \
  -d '{"UICulture":"en-US","MetadataCountryCode":"US","PreferredMetadataLanguage":"en"}' >/dev/null
curl -fsS "$BASE/Startup/User" >/dev/null   # wizard expects this GET before the POST
curl -fsS -X POST "$BASE/Startup/User" -H 'Content-Type: application/json' \
  -d "{\"Name\":\"$USER_NAME\",\"Password\":\"$USER_PW\"}" >/dev/null
curl -fsS -X POST "$BASE/Startup/RemoteAccess" -H 'Content-Type: application/json' \
  -d '{"EnableRemoteAccess":true,"EnableAutomaticPortMapping":false}' >/dev/null
curl -fsS -X POST "$BASE/Startup/Complete" >/dev/null

echo "== 4/7 authenticating =="
AUTH=$(curl -fsS -X POST "$BASE/Users/AuthenticateByName" \
  -H "Authorization: $AUTH_HDR" -H 'Content-Type: application/json' \
  -d "{\"Username\":\"$USER_NAME\",\"Pw\":\"$USER_PW\"}")
TOKEN=$(printf '%s' "$AUTH" | jget AccessToken)
USER_ID=$(printf '%s' "$AUTH" | jget User.Id)
[ -n "$TOKEN" ] || { echo "auth failed"; exit 1; }

echo "== 5/7 adding dev-media library (/media) + scanning =="
curl -fsS -X POST "$BASE/Library/VirtualFolders?name=dev-media&collectionType=movies&refreshLibrary=true" \
  -H "X-Emby-Token: $TOKEN" -H 'Content-Type: application/json' \
  -d '{"LibraryOptions":{"PathInfos":[{"Path":"/media"}]}}' >/dev/null
curl -fsS -X POST "$BASE/Library/Refresh" -H "X-Emby-Token: $TOKEN" >/dev/null || true

echo "== 6/7 waiting for the scan to index the clips =="
# Select by file Path, not Name: Jellyfin's online metadata matcher collapses
# all three H.264 fixtures to one title, so Name is ambiguous. Wait until BOTH
# the long clip and the 1080p clip (larger, scans later) are indexed. Fields=Path
# also brings MediaStreams so we read the subtitle index without a second call.
# Strict path matches only (no its[0] here — that raced the scan and aliased the
# long clip to whatever indexed first). The 273 MB 1080p file scans LAST, so wait
# on it; then fall back to its[0] for the long clip only after the loop.
ITEM_ID=""; P1080_ID=""; SUB_IDX=""; FIRST_ID=""
for i in $(seq 1 90); do
  ITEMS=$(curl -fsS "$BASE/Items?Recursive=true&IncludeItemTypes=Movie&userId=$USER_ID&Fields=Path,MediaStreams" \
    -H "X-Emby-Token: $TOKEN")
  eval "$(printf '%s' "$ITEMS" | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const its=(JSON.parse(s).Items||[]);const byPath=re=>its.find(i=>re.test(i.Path||""));const long=byPath(/h264-aac-long/i)||byPath(/long/i);const p=byPath(/1080p/i);const sub=p&&(p.MediaStreams||[]).find(x=>x.Type==="Subtitle");process.stdout.write(`ITEM_ID=${long?long.Id:""}\nP1080_ID=${p?p.Id:""}\nSUB_IDX=${sub?sub.Index:""}\nFIRST_ID=${its[0]?its[0].Id:""}\n`)})')"
  # Done once the last-to-scan 1080p clip is in; also stop if there's simply no
  # 1080p fixture on disk (long is in and the item count has settled).
  [ -n "$P1080_ID" ] && break
  [ -n "$ITEM_ID" ] && [ "$i" -ge 20 ] && break
  sleep 1
done
ITEM_ID="${ITEM_ID:-$FIRST_ID}"
[ -n "$ITEM_ID" ] || { echo "scan produced no movie items"; exit 1; }

echo "== 7/7 minting agent API key =="
curl -fsS -X POST "$BASE/Auth/Keys?app=r8er-agent" -H "X-Emby-Token: $TOKEN" >/dev/null || true
API_KEY=$(curl -fsS "$BASE/Auth/Keys" -H "X-Emby-Token: $TOKEN" \
  | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const k=(JSON.parse(s).Items||[]).find(x=>x.AppName==="r8er-agent");process.stdout.write(k?k.AccessToken:"")})')
[ -n "$API_KEY" ] || API_KEY="$TOKEN"   # fall back to the session token

# Persist for the harness / agent. Git-ignored: throwaway dev creds.
OUT="dev/.jellyfin.json"
node -e 'const fs=require("fs");fs.writeFileSync(process.argv[1],JSON.stringify({base:process.argv[2],user:process.argv[3],userId:process.argv[4],token:process.argv[5],apiKey:process.argv[6],longItemId:process.argv[7],p1080ItemId:process.argv[8]||null,subtitleIndex:process.argv[9]||null},null,2)+"\n")' \
  "$OUT" "$BASE" "$USER_NAME" "$USER_ID" "$TOKEN" "$API_KEY" "$ITEM_ID" "$P1080_ID" "$SUB_IDX"

echo
echo "Jellyfin ready."
echo "  admin:      $USER_NAME / $USER_PW"
echo "  user id:    $USER_ID"
echo "  api key:    $API_KEY"
echo "  long clip:  $ITEM_ID"
echo "  1080p+subs: ${P1080_ID:-<none — run gen-test-media.sh then re-run>} (subtitle index: ${SUB_IDX:-none})"
echo "  wrote:      $OUT"
echo
echo "HLS URL to play through the tunnel (origin-relative):"
echo "  /Videos/$ITEM_ID/master.m3u8?mediaSourceId=$ITEM_ID&deviceId=r8er-poc&api_key=$API_KEY&videoCodec=h264&audioCodec=aac"
if [ -n "$P1080_ID" ]; then
  echo
  echo "Phone play URL (1080p + subs over the away path) — open on the phone:"
  echo "  https://r8er.up.railway.app/play.html#item=$P1080_ID&key=$API_KEY${SUB_IDX:+&subs=$SUB_IDX}"
fi

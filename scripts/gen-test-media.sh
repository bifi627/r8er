#!/usr/bin/env bash
# Generate deterministic, license-free test media into dev-media/ (gitignored).
# Requires ffmpeg. Runs in Git Bash, WSL, devcontainer, CI.
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p dev-media

# Plays-everywhere baseline (H.264 + AAC, MP4)
ffmpeg -y -f lavfi -i "testsrc=duration=30:size=1280x720:rate=30" \
  -f lavfi -i "sine=frequency=440:duration=30" \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \
  dev-media/h264-aac.mp4

# Forces Jellyfin to transcode for a browser (HEVC + AC3, MKV)
ffmpeg -y -f lavfi -i "testsrc=duration=30:size=1280x720:rate=30" \
  -f lavfi -i "sine=frequency=440:duration=30" \
  -c:v libx265 -pix_fmt yuv420p -c:a ac3 -shortest \
  dev-media/hevc-ac3.mkv

# Long clip for seek testing (~10 min, H.264 + AAC)
ffmpeg -y -f lavfi -i "testsrc=duration=600:size=1280x720:rate=30" \
  -f lavfi -i "sine=frequency=440:duration=600" \
  -c:v libx264 -pix_fmt yuv420p -c:a aac -shortest \
  dev-media/h264-aac-long.mp4

echo "Test media written to dev-media/"

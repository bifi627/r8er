#!/usr/bin/env bash
# Golden-path check. Mirrors .github/workflows/ci.yml — if they disagree, that's a harness bug.
# Run this and READ the output before claiming any work done.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== backend: format + build + test =="
dotnet format backend/R8er.slnx --verify-no-changes
dotnet test backend/R8er.slnx --nologo

echo "== frontend: format + lint + typecheck + build =="
npm run format:check --prefix frontend
npm run lint --prefix frontend
npm run build --prefix frontend

echo "== agent: fmt + vet + build + test =="
if command -v go >/dev/null 2>&1; then
  (cd agent && test -z "$(gofmt -l .)" || { echo "gofmt needed:"; gofmt -l .; exit 1; }
   go vet ./... && go build ./... && go test ./...)
else
  # ponytail: Go absent on the primary Windows machine; CI still runs it
  echo "!! SKIPPED: go not installed locally — agent is UNVERIFIED here, CI will run it" >&2
fi

echo "== compose config =="
docker compose config --quiet
docker compose --profile relay config --quiet

echo "== ALL CHECKS PASSED =="

#!/usr/bin/env bash
# Telltale quality gate.
#
# One command that must pass before any commit. Mirrors .github/workflows/ci.yml
# so a green local run means a green CI run.
#
#   bash scripts/quality-gate.sh
set -euo pipefail

cd "$(dirname "$0")/.."

echo "==> Backend: dotnet test"
dotnet test Telltale.slnx

echo "==> Frontend: install"
if [ ! -d frontend/node_modules ]; then
  (cd frontend && npm ci)
fi

echo "==> Frontend: typecheck + build"
(cd frontend && npm run build)

echo "==> Frontend: vitest"
(cd frontend && npm test)

echo "==> Quality gate passed"

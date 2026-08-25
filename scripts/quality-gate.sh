#!/usr/bin/env bash
# Telltale quality gate.
#
# One command that must pass before any commit. Mirrors .github/workflows/ci.yml
# so a green local run means a green CI run.
#
#   bash scripts/quality-gate.sh
set -euo pipefail

cd "$(dirname "$0")/.."

# Before the backend, because building the viewer compiles the frontend into it.
#
# Unconditional, and npm ci rather than npm install. The guard this replaces ran
# the install only when frontend/node_modules was absent, which is a weaker
# claim than the one the skip depends on: an interrupted install, a partly
# copied worktree or a package.json that has gained a dependency all leave the
# directory present and its contents wrong. Nothing repaired that, and the first
# sign of it was the TypeScript build failing with missing-module errors that
# read as a code fault rather than a setup one.
echo "==> Frontend: install"
(cd frontend && npm ci)

echo "==> Backend: dotnet test"
dotnet test Telltale.slnx

echo "==> Frontend: typecheck + build"
(cd frontend && npm run build)

echo "==> Frontend: vitest"
(cd frontend && npm test)

echo "==> Quality gate passed"

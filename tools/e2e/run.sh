#!/usr/bin/env bash
# Browser end-to-end tests (src/frontend/e2e) against the running local stack: TC-P1-IAM-001/002, tokens in memory.
# Needs: `docker compose -f deploy/docker/compose.yaml up -d --wait`, `Karry.Host migrate` and `seed-demo` done
# (deploy/docker/smoke-test.sh with KEEP_STACK=1 does all three). Starts Karry.Host (:5000) and both apps
# (vite preview :5173 portal, :5174 field), runs Playwright, stops what it started.
#   E2E_INSTALL_DEPS=1  also install Chromium's system libraries (CI runners)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
frontend="$root/src/frontend"
pids=()
cleanup() { for pid in "${pids[@]}"; do kill "$pid" 2>/dev/null || true; done; }
trap cleanup EXIT

wait_for() { # url, name
  for _ in $(seq 1 120); do curl -fsS -o /dev/null "$1" && return 0; sleep 1; done
  echo "FAIL  $2 did not answer at $1" >&2; return 1
}

dotnet build "$root/src/backend/Karry.Host" -c Release >/dev/null
(cd "$root/src/backend/Karry.Host/bin/Release/net10.0" && DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development \
  exec dotnet Karry.Host.dll --urls http://127.0.0.1:5000) >"${TMPDIR:-/tmp}/karry-e2e-host.log" 2>&1 &
pids+=("$!")

cd "$frontend"
pnpm --filter @karry/portal build >/dev/null
pnpm --filter @karry/field build >/dev/null
pnpm --filter @karry/portal exec vite preview >"${TMPDIR:-/tmp}/karry-e2e-portal.log" 2>&1 &
pids+=("$!")
pnpm --filter @karry/field exec vite preview >"${TMPDIR:-/tmp}/karry-e2e-field.log" 2>&1 &
pids+=("$!")

wait_for http://127.0.0.1:5000/health/ready "Karry.Host"
wait_for http://localhost:5173/ "portal (vite preview)"
wait_for http://localhost:5174/ "field (vite preview)"
# Branding through the app origin, as the login page reads it.
wait_for http://localhost:5173/api/v1/tenancy/public/demo-carriere-a "API through the portal origin"

if [[ "${E2E_INSTALL_DEPS:-0}" == "1" ]]; then
  pnpm --filter @karry/e2e exec playwright install --with-deps chromium
else
  pnpm --filter @karry/e2e exec playwright install chromium
fi
pnpm --filter @karry/e2e e2e

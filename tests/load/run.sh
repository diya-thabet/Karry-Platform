#!/usr/bin/env bash
# The load suite (P1.18a, docs/05-quality/load-testing.md) against a running local stack: migrated and seeded
# (`Karry.Host migrate`, `seed-demo`), Keycloak up, Karry.Host on :5000 started with the load-run limits:
#   RateLimits__PerPersonPerMinute=100000 RateLimits__AnonymousPerAddressPerMinute=100000
# (six demo accounts stand for many people, and setup enrols the tablets from one address; the tablet and company
# limits stay as in production). Runs H-L1 (devices.js: burst and shift), TC-P1-TEN-005 (tenants.js mixed) and the
# spike (tenants.js spike), one at a time, and keeps each summary in tests/load/results/<time>/ (git-ignored).
# Exit code: non-zero when any threshold fails. TABLETS, REQUESTS, VUS and SPIKE_VUS change the sizes.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
k6="${K6:-$(command -v k6 || echo "$HOME/.local/bin/k6")}"
api="${KARRY_API:-http://127.0.0.1:5000}"
keycloak="${KARRY_KEYCLOAK:-http://localhost:8080}"
[ -x "$k6" ] || { echo "k6 not found: tools/ci/install-tool.sh k6" >&2; exit 2; }
curl -fsS -o /dev/null "$api/health/ready" || { echo "Karry.Host is not ready at $api" >&2; exit 2; }
curl -fsS -o /dev/null "$keycloak/realms/karry/.well-known/openid-configuration" || { echo "Keycloak is not up at $keycloak" >&2; exit 2; }

out="$here/results/$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$out"
status=0
run() {
  local name="$1"; shift
  echo "== $name"
  if "$k6" run --quiet --summary-export "$out/$name.json" "$@" > "$out/$name.txt" 2>&1; then
    echo "   passed"
  else
    status=1
    echo "   FAILED (see $out/$name.txt)"
  fi
  grep -E "^\s+(✓|✗) '" "$out/$name.txt" || true
}

run devices "$here/devices.js"
run tenants-mixed -e SCENARIO=mixed "$here/tenants.js"
run tenants-spike -e SCENARIO=spike "$here/tenants.js"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  python3 "$here/summary.py" "$out" >> "$GITHUB_STEP_SUMMARY"
fi
python3 "$here/summary.py" "$out"
exit "$status"

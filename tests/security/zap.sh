#!/usr/bin/env bash
# OWASP ZAP baseline (hardening plan H-W1) against the running local stack: the portal and the field app (production
# build, vite preview) are spidered and observed, the API is visited from its OpenAPI description in safe mode. Only
# passive rules run: no attack is sent, nothing is written. Fails on any high finding, and on a medium one that is
# not accepted, with its reason, in tests/security/zap-accepted.tsv.
# Usage: tests/security/zap.sh [results directory]   (needs Docker, Karry.Host on :5000, previews on :5173 and :5174)
set -euo pipefail

# A dated weekly build at least a week old, pinned by digest (supply chain rule; the moving tags are re-pushed weekly).
image="zaproxy/zap-stable:20261026@sha256:030d3df57735dacbb83464854758e674dd9ad01d79d12a129a8e58b1fd384238"
here="$(cd "$(dirname "$0")" && pwd)"
out="${1:-$here/results/$(date -u +%Y%m%dT%H%M%SZ)}"
api="${KARRY_API_URL:-http://localhost:5000}"
mkdir -p "$out"
out="$(cd "$out" && pwd)" # docker -v takes a relative path for a named volume, not a folder
chmod 777 "$out" # ZAP runs as its own user inside the container

for url in "$api/health/ready" http://localhost:5173/ http://localhost:5174/; do
  curl -fsS -o /dev/null "$url" || { echo "not reachable: $url (start the local stack, Karry.Host and both previews)" >&2; exit 2; }
done

# Exit codes of the ZAP scripts: 0 clean, 1 failures, 2 warnings, 3 the scan itself broke. Findings are judged by
# zap_summary.py from the JSON reports, so only 3 stops the run here.
scan() {
  local status=0
  docker run --rm --network host -v "$out:/zap/wrk:rw" "$image" "$@" || status=$?
  if [[ "$status" -ge 3 ]]; then
    echo "ZAP failed to scan ($*)" >&2
    exit 3
  fi
}

scan zap-baseline.py -t http://localhost:5173/ -m 2 -J portal.json -r portal.html -I
scan zap-baseline.py -t http://localhost:5174/ -m 2 -J field.json -r field.html -I
# P2.12a slice 5: the public verification page (US-P2-SIG-06), opened from a document's QR before any sign-in. The
# spider cannot reach it from the portal's home; any envelope ID serves the same page and headers (securityHeaders
# "verify"), and the token after # never reaches the server. Its API (POST /api/v1/documents/verify/{id}) is in the
# OpenAPI scan below.
scan zap-baseline.py -t "http://localhost:5173/verify/01930000-0000-7000-8000-000000000000#zap-baseline-token" -m 1 -J verify.json -r verify.html -I
scan zap-api-scan.py -t "$api/openapi/v1.json" -f openapi -S -J api.json -r api.html -I

# The table also goes to summary.md (the nightly job shows it on the run page).
python3 "$here/zap_summary.py" "$out" "$here/zap-accepted.tsv" | tee "$out/summary.md"

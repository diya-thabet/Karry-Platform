#!/usr/bin/env bash
# Hardening plan H-L3: after the load suite, the audit ledger of every company still verifies (hash chain, sequence,
# and anchors when Trust__Anchoring__* is set). Thousands of concurrent writes must not leave a gap, a fork or a
# re-ordered entry. Runs `karry-ledger verify` (tools/ledger-verifier) for each tenant that has a ledger, against the
# local stack (`deploy/docker/compose.yaml`); the runtime role is enough (row-level security scopes each run).
# Exit code: non-zero when any ledger is broken or cannot be checked.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
compose=(docker compose -f "$root/deploy/docker/compose.yaml")
connection="${KARRY_LEDGER_CONNECTION:-$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["ConnectionStrings"]["Karry"])' \
  "$root/src/backend/Karry.Host/appsettings.Development.json")}"
export KARRY_LEDGER_CONNECTION="$connection"

# Listing every tenant crosses tenants: only the administrator may (inside the database container, never exposed).
mapfile -t tenants < <("${compose[@]}" exec -T postgres psql -U postgres -d karry -Atc "SELECT tenant_id FROM audit.ledger_heads ORDER BY tenant_id")
[ "${#tenants[@]}" -gt 0 ] || { echo "no ledger found: is the stack seeded?" >&2; exit 2; }

dotnet build "$root/tools/ledger-verifier/Karry.LedgerVerifier" -c Release --nologo -v quiet > /dev/null
verifier=(dotnet "$root/tools/ledger-verifier/Karry.LedgerVerifier/bin/Release/net10.0/karry-ledger.dll")
status=0
for tenant in "${tenants[@]}"; do
  "${verifier[@]}" verify --tenant "$tenant" || status=1
done
echo "== ${#tenants[@]} ledgers checked: $([ "$status" = 0 ] && echo "all intact" || echo "BROKEN ledger(s) above")"
exit "$status"

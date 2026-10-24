#!/usr/bin/env bash
# TC-P1-PLT-004 (+ TC-P1-PLT-012 against the real stack): start the local stack and prove every service works,
# including S3 Object Lock in compliance mode (ADR-0017) and Karry.Host readiness.
# P1.4: a request to Karry.Host is visible end-to-end in Grafana (trace, metrics, logs, dashboards, alert rules).
#   SKIP_HOST=1   skip the Karry.Host readiness check (no .NET SDK available)
#   KEEP_STACK=1  leave the stack running afterwards
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
compose=(docker compose -f "$root/deploy/docker/compose.yaml")
aws_image="amazon/aws-cli:2.38.5"
host_pid=""
failures=0

cleanup() {
  [[ -n "$host_pid" ]] && kill "$host_pid" 2>/dev/null || true
  if [[ "${KEEP_STACK:-0}" != "1" ]]; then "${compose[@]}" down -v --remove-orphans >/dev/null 2>&1 || true; fi
}
trap cleanup EXIT

pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1" >&2; failures=$((failures + 1)); }
check() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then pass "$name"; else fail "$name"; fi; }
refused() { local name="$1"; shift; if "$@" >/dev/null 2>&1; then fail "$name (operation was allowed)"; else pass "$name"; fi; }

# Loki (in lgtm) refuses log writes when its disk is more than 90 % full: say so up front.
docker_root="$(docker info --format '{{.DockerRootDir}}' 2>/dev/null || true)"
disk_used="$({ df --output=pcent "${docker_root:-/}" 2>/dev/null || df --output=pcent /; } | tail -1 | tr -dc '0-9')"
if [[ -n "$disk_used" && "$disk_used" -ge 85 ]]; then echo "WARN  Docker disk ${disk_used}% used: Loki stops accepting logs above 90 %" >&2; fi

echo "Starting the local stack…"
if ! "${compose[@]}" up -d --wait --wait-timeout 300; then
  "${compose[@]}" ps
  "${compose[@]}" logs --tail 80
  exit 1
fi
pass "all services healthy"

# PostgreSQL: the 4 extensions and the runtime role.
psql_su() { "${compose[@]}" exec -T -e PGPASSWORD="${KARRY_PG_SUPERUSER_PASSWORD:-karry-local-only}" postgres psql -h 127.0.0.1 -U postgres -d karry -v ON_ERROR_STOP=1 -Atc "$1"; }
extensions="$(psql_su "SELECT string_agg(extname, ',' ORDER BY extname) FROM pg_extension WHERE extname IN ('postgis','timescaledb','vector','pgcrypto')")"
if [[ "$extensions" == "pgcrypto,postgis,timescaledb,vector" ]]; then pass "postgres extensions ($extensions)"; else fail "postgres extensions (got '$extensions')"; fi
check "karry_app can connect" "${compose[@]}" exec -T -e PGPASSWORD="${KARRY_APP_PASSWORD:-karry-local-only}" postgres psql -h 127.0.0.1 -U karry_app -d karry -Atc "SELECT 1"
role="$(psql_su "SELECT rolsuper::text || ',' || rolbypassrls::text FROM pg_roles WHERE rolname = 'karry_app'")"
if [[ "$role" == "false,false" ]]; then pass "karry_app is neither superuser nor BYPASSRLS"; else fail "karry_app privileges (got '$role')"; fi
owner_role="$(psql_su "SELECT rolsuper::text || ',' || rolbypassrls::text FROM pg_roles WHERE rolname = 'karry_owner'")"
if [[ "$owner_role" == "false,false" ]]; then pass "karry_owner exists (migrations), neither superuser nor BYPASSRLS"; else fail "karry_owner role (got '$owner_role')"; fi

# Keycloak, Mailpit, Valkey.
check "keycloak serves OIDC discovery" curl -fsS http://127.0.0.1:8080/realms/master/.well-known/openid-configuration
# P1.8: the Karry realm and login theme from deploy/keycloak (imported on first start).
check "keycloak imported the karry realm" curl -fsS http://127.0.0.1:8080/realms/karry/.well-known/openid-configuration
login_page="$(curl -fsS 'http://127.0.0.1:8080/realms/karry/protocol/openid-connect/auth?client_id=karry-portal&response_type=code&scope=openid&redirect_uri=http%3A%2F%2Flocalhost%3A5173%2Fauth%2Fcallback&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256' || true)"
if [[ "$login_page" == *karry-branding.js* && "$login_page" == *'lang="fr"'* ]]; then pass "keycloak login page uses the karry theme (fr)"; else fail "keycloak login page theme"; fi
check "mailpit API answers" curl -fsS http://127.0.0.1:8025/api/v1/info
check "valkey answers PING" bash -c "${compose[*]} exec -T valkey valkey-cli ping | grep -q PONG"

# S3 Object Lock (compliance mode): locked versions cannot be deleted and retention cannot be shortened.
aws() {
  docker run --rm --network host \
    -e AWS_ACCESS_KEY_ID=karry-local-access -e AWS_SECRET_ACCESS_KEY=karry-local-secret-only-for-dev -e AWS_DEFAULT_REGION=us-east-1 \
    "$aws_image" --endpoint-url http://127.0.0.1:8333 "$@"
}
bucket="smoke-anchors-$(date +%s)"
retain_until="$(date -u -d '+1 day' +%Y-%m-%dT%H:%M:%SZ)"
shorter="$(date -u -d '+1 hour' +%Y-%m-%dT%H:%M:%SZ)"
check "s3 create bucket with Object Lock" aws s3api create-bucket --bucket "$bucket" --object-lock-enabled-for-bucket
version_id="$(aws s3api put-object --bucket "$bucket" --key chain-head.json --body /etc/hostname \
  --object-lock-mode COMPLIANCE --object-lock-retain-until-date "$retain_until" --query VersionId --output text || true)"
if [[ -n "$version_id" && "$version_id" != "None" ]]; then
  pass "s3 put object under COMPLIANCE retention"
  refused "s3 delete of a locked version is refused" aws s3api delete-object --bucket "$bucket" --key chain-head.json --version-id "$version_id"
  refused "s3 shortening COMPLIANCE retention is refused" aws s3api put-object-retention --bucket "$bucket" --key chain-head.json --version-id "$version_id" \
    --retention "Mode=COMPLIANCE,RetainUntilDate=$shorter"
  check "s3 locked version still readable" aws s3api head-object --bucket "$bucket" --key chain-head.json --version-id "$version_id"
else
  # Without a locked version the lock checks below would pass vacuously, so they are not run.
  fail "s3 put object under COMPLIANCE retention (lock checks not run)"
fi

# Karry.Host readiness against the real stack (Development settings point at this stack).
if [[ "${SKIP_HOST:-0}" != "1" ]]; then
  dotnet build "$root/src/backend/Karry.Host" -c Release >/dev/null
  # Migrations as their own step, as karry_owner (ConnectionStrings:KarryOwner in Development settings).
  if (cd "$root/src/backend/Karry.Host/bin/Release/net10.0" && DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development dotnet Karry.Host.dll migrate >/dev/null); then
    owner="$(psql_su "SELECT tableowner FROM pg_tables WHERE schemaname = 'platform' AND tablename = 'idempotency_records'")"
    if [[ "$owner" == "karry_owner" ]]; then pass "Karry.Host migrate: platform schema created, owned by karry_owner"; else fail "Karry.Host migrate: table owner '$owner'"; fi
    # Demo tenants (US-P1-TEN-01), twice: provisioning is idempotent.
    (cd "$root/src/backend/Karry.Host/bin/Release/net10.0" && for _ in 1 2; do DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development dotnet Karry.Host.dll seed-demo >/dev/null || exit 1; done) || fail "Karry.Host seed-demo"
    demo="$(psql_su "SELECT string_agg(slug, ',' ORDER BY slug) FROM tenancy.tenants WHERE slug LIKE 'demo-carriere-%'")"
    if [[ "$demo" == "demo-carriere-a,demo-carriere-b" ]]; then pass "seed-demo twice: exactly the 2 demo tenants"; else fail "seed-demo (got '$demo')"; fi
    ledger="$(psql_su "SELECT event_type FROM audit.ledger_entries e JOIN tenancy.tenants t ON t.id = e.tenant_id WHERE t.slug = 'demo-carriere-a' AND seq = 1")"
    if [[ "$ledger" == "tenancy.tenant_provisioned.v1" ]]; then pass "audit ledger: demo tenant A opened with entry 1"; else fail "audit ledger (got '$ledger')"; fi
    # P1.8: one user per default role in each demo tenant (PLT-02 AC2), active, with their creation in the ledger.
    users="$(psql_su "SELECT count(*) FILTER (WHERE u.status = 'Active') || '/' || count(*) FROM identity.users u JOIN tenancy.tenants t ON t.id = u.tenant_id WHERE t.slug = 'demo-carriere-a'")"
    if [[ "$users" == "12/12" ]]; then pass "seed-demo: 12 active demo users (one per default role)"; else fail "seed-demo users (got '$users')"; fi
    identity_events="$(psql_su "SELECT count(*) FROM audit.ledger_entries e JOIN tenancy.tenants t ON t.id = e.tenant_id WHERE t.slug = 'demo-carriere-a' AND e.event_type LIKE 'identity.%'")"
    if [[ "$identity_events" -ge 12 ]]; then pass "audit ledger: identity events recorded ($identity_events)"; else fail "audit ledger identity events (got '$identity_events')"; fi

    # WORM anchoring (US-P1-AUD-02): one run, then the verifier checks the chain against the anchor in SeaweedFS.
    dotnet build "$root/src/backend/Karry.Workers" -c Release >/dev/null && dotnet build "$root/tools/ledger-verifier/Karry.LedgerVerifier" -c Release >/dev/null
    if (cd "$root/src/backend/Karry.Workers/bin/Release/net10.0" && DOTNET_ENVIRONMENT=Development dotnet Karry.Workers.dll anchor-now >/dev/null); then
      pass "Karry.Workers anchor-now: every tenant anchored"
    else
      fail "Karry.Workers anchor-now"
    fi
    # Outbox dispatchers (P1.8): Tenancy's tenant_provisioned sets the tenant up in Keycloak (organization, app addresses).
    if (cd "$root/src/backend/Karry.Workers/bin/Release/net10.0" && DOTNET_ENVIRONMENT=Development dotnet Karry.Workers.dll dispatch-now >/dev/null); then
      pass "Karry.Workers dispatch-now: every outbox drained"
    else
      fail "Karry.Workers dispatch-now"
    fi
    if (cd "$root/src/backend/Karry.Workers/bin/Release/net10.0" && DOTNET_ENVIRONMENT=Development dotnet Karry.Workers.dll import-sign-ins >/dev/null); then
      pass "Karry.Workers import-sign-ins: Keycloak events readable by the Identity service account"
    else
      fail "Karry.Workers import-sign-ins"
    fi
    orgs="$(python3 - <<'PY' || true
import json, urllib.parse, urllib.request
token = json.load(urllib.request.urlopen(urllib.request.Request(
    "http://127.0.0.1:8080/realms/karry/protocol/openid-connect/token",
    urllib.parse.urlencode({"grant_type": "client_credentials", "client_id": "karry-identity", "client_secret": "identity-local-only"}).encode())))["access_token"]
request = urllib.request.Request("http://127.0.0.1:8080/admin/realms/karry/organizations", headers={"Authorization": "Bearer " + token})
print(",".join(sorted(o["alias"] for o in json.load(urllib.request.urlopen(request)) if o["alias"].startswith("demo-carriere-"))))
PY
)"
    if [[ "$orgs" == "demo-carriere-a,demo-carriere-b" ]]; then pass "keycloak organizations for both demo tenants"; else fail "keycloak organizations (got '$orgs')"; fi
    tenant_a="$(psql_su "SELECT id FROM tenancy.tenants WHERE slug = 'demo-carriere-a'")"
    verify="$(Trust__Anchoring__ServiceUrl=http://127.0.0.1:8333 Trust__Anchoring__Bucket=karry-anchors \
      Trust__Anchoring__AccessKey=karry-local-access Trust__Anchoring__SecretKey=karry-local-secret-only-for-dev \
      Trust__Anchoring__SigningKeyFile='~/.karry/anchor-signing-dev.pem' \
      dotnet "$root/tools/ledger-verifier/Karry.LedgerVerifier/bin/Release/net10.0/karry-ledger.dll" verify --tenant "$tenant_a" \
      --connection "Host=127.0.0.1;Port=5432;Database=karry;Username=karry_app;Password=${KARRY_APP_PASSWORD:-karry-local-only}" || true)"
    if [[ "$verify" == *"OK tenant"*"1 anchors matched"* ]]; then pass "karry-ledger verify: chain intact and matches its WORM anchor"; else fail "karry-ledger verify (got '$verify')"; fi
  else
    fail "Karry.Host migrate"
  fi
  # Run the built assembly directly (exec) so $host_pid is the host itself and cleanup stops it.
  (cd "$root/src/backend/Karry.Host/bin/Release/net10.0" &&
    ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5080 exec dotnet Karry.Host.dll >/dev/null 2>&1) &
  host_pid=$!
  ready=0
  for _ in $(seq 1 60); do
    if curl -fsS http://127.0.0.1:5080/health/ready >/dev/null 2>&1; then ready=1; break; fi
    sleep 1
  done
  if [[ "$ready" == "1" ]]; then pass "Karry.Host /health/ready = 200 against the stack"; else fail "Karry.Host /health/ready against the stack"; fi

  # Observability end to end: the correlation ID returned to the caller is the trace ID to look up in Tempo.
  correlation_id="$(curl -fsS -D - -o /dev/null http://127.0.0.1:5080/openapi/v1.json | tr -d '\r' | awk 'tolower($1) == "x-correlation-id:" {print $2}')"
  if [[ "$correlation_id" =~ ^[0-9a-f]{32}$ ]]; then
    pass "Karry.Host returns X-Correlation-Id ($correlation_id)"
    if python3 -I "$root/deploy/observability/verify_stack.py" "$correlation_id"; then pass "request visible end to end in Grafana"; else fail "request visible end to end in Grafana"; fi
  else
    fail "Karry.Host returns X-Correlation-Id (got '$correlation_id')"
  fi
fi

if [[ "$failures" -gt 0 ]]; then
  echo "TC-P1-PLT-004: $failures check(s) failed" >&2
  "${compose[@]}" logs --tail 40 s3 keycloak >&2 || true
  "${compose[@]}" logs lgtm 2>&1 | grep -iE "error|warn|refus|reject|fail" | tail -60 >&2 || true
  exit 1
fi
echo "TC-P1-PLT-004 passed."

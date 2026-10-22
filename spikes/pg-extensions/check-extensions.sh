#!/usr/bin/env bash
# WP-P0-13: verify that a PostgreSQL image provides every extension Karry depends on
# (PostGIS, TimescaleDB, pgvector, pgcrypto) and report their versions.
# Usage: spikes/pg-extensions/check-extensions.sh timescale/timescaledb-ha:pg17
set -euo pipefail

image="${1:?usage: $0 <postgres-image>}"
name="karry-ext-check-$$"
required=(postgis timescaledb vector pgcrypto)

cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker run -d --name "$name" -e POSTGRES_PASSWORD=spike-only -e POSTGRES_DB=karry "$image" >/dev/null

# The image's entrypoint first runs a temporary server (Unix socket only) to initialise
# the database, then restarts it. Checking readiness over TCP waits for the real server;
# a socket check can succeed against the temporary one and race the restart.
ready() { docker exec "$name" pg_isready -h 127.0.0.1 -U postgres -d karry >/dev/null 2>&1; }
for _ in $(seq 1 90); do
  if ready; then break; fi
  sleep 2
done
if ! ready; then
  echo "PostgreSQL in ${image} did not become ready; container logs:" >&2
  docker logs "$name" >&2 || true
  exit 1
fi

psql_exec() { docker exec -e PGPASSWORD=spike-only "$name" psql -h 127.0.0.1 -U postgres -d karry -v ON_ERROR_STOP=1 -Atc "$1"; }

server_version="$(psql_exec 'SHOW server_version')"
report="### ${image}\n\n| Extension | Version |\n|---|---|\n| PostgreSQL server | ${server_version} |\n"
status=0
for ext in "${required[@]}"; do
  if psql_exec "CREATE EXTENSION IF NOT EXISTS ${ext} CASCADE" >/dev/null 2>&1; then
    version="$(psql_exec "SELECT extversion FROM pg_extension WHERE extname = '${ext}'")"
    report+="| ${ext} | ${version} |\n"
  else
    report+="| ${ext} | **MISSING** |\n"
    status=1
  fi
done

printf '%b\n' "$report"
if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then printf '%b\n' "$report" >> "$GITHUB_STEP_SUMMARY"; fi
exit "$status"

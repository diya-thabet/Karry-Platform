#!/usr/bin/env bash
# P2.12a burst, queue gate: after the shift change, what the batches handed to the other modules (review items,
# integrity flags, notices, ledger entries: every module's outbox, dispatched by Karry.Workers) is handled within
# QUEUE_SECONDS (10) and nothing is dead-lettered. Counts across every schema's outbox_messages as the database
# administrator, inside the database container of the local stack (deploy/docker/compose.yaml).
# Usage: SINCE=<UTC time the run started> tests/load/outbox-drained.sh [seconds]
# Exit code: non-zero when messages wait longer, or any was dead-lettered since SINCE.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
compose=(docker compose -f "$root/deploy/docker/compose.yaml")
limit="${1:-${QUEUE_SECONDS:-10}}"
psql() { "${compose[@]}" exec -T postgres psql -U postgres -d karry -Atc "$1"; }

mapfile -t tables < <(psql "SELECT table_schema || '.outbox_messages' FROM information_schema.tables WHERE table_name = 'outbox_messages' ORDER BY 1")
[ "${#tables[@]}" -gt 0 ] || { echo "no outbox table found: is the stack migrated?" >&2; exit 2; }
count() { # pending | dead
  local where="processed_at IS NULL AND dead_lettered_at IS NULL"
  [ "$1" = dead ] && where="dead_lettered_at >= '${SINCE:?SINCE: the UTC time the run started}'::timestamptz"
  local query="" table
  for table in "${tables[@]}"; do query+="${query:+ + }(SELECT count(*) FROM $table WHERE $where)"; done
  psql "SELECT $query"
}

start=$(date +%s)
while :; do
  pending="$(count pending)"
  waited=$(( $(date +%s) - start ))
  [ "$pending" = 0 ] && break
  if [ "$waited" -ge "$limit" ]; then
    echo "FAIL  $pending outbox message(s) still waiting after ${limit}s (${#tables[@]} outboxes): is Karry.Workers running?" >&2
    exit 1
  fi
  sleep 1
done

dead="$(count dead)"
if [ "$dead" != 0 ]; then
  echo "FAIL  $dead outbox message(s) dead-lettered since $SINCE" >&2
  exit 1
fi
echo "PASS  every outbox drained ${waited}s after the shift change (limit ${limit}s), none dead-lettered"

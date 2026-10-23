---
title: "Runbook: KarryDatabaseCpuHigh"
area: operations
tags: [runbook, alert, database, postgresql, cpu]
source: "P1.4, US-P1-OPS-02 AC3"
status: baseline
last_updated: 2026-10-23
---

# KarryDatabaseCpuHigh

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** CPU of the **PostgreSQL host** above **80 %** for **15 minutes** (`node_cpu_seconds_total{karry_role="database"}`, severity `page`). The metric comes from node_exporter on the database host, or from the managed database's exporter relabelled to the same name; it is wired when hosting is chosen (P1.17).
- **Impact:** every query slows down; API latency and error alerts usually follow; field sync and weighbridge tickets queue up (they are retried, not lost).
- **Diagnosis:**
  1. Top queries by total time (requires `pg_stat_statements`):
     ```sql
     SELECT calls, round(total_exec_time) AS total_ms, round(mean_exec_time, 1) AS mean_ms, left(query, 120)
     FROM pg_stat_statements ORDER BY total_exec_time DESC LIMIT 15;
     ```
  2. Running now: `SELECT pid, now() - query_start AS running, state, left(query, 120) FROM pg_stat_activity WHERE state <> 'idle' ORDER BY running DESC;`
  3. Correlate with **Karry · API (RED per module)** (which module's traffic grew) and **Karry · Platform** → *DB command p95*.
  4. Autovacuum or a large import/backfill running? `SELECT * FROM pg_stat_progress_vacuum;`, import jobs in the worker logs.
- **Mitigation:**
  - A runaway query → `SELECT pg_cancel_backend(<pid>);` (cancel, not terminate, first).
  - Bulk import or backfill → pause it; schedule outside shift changes.
  - Sustained organic load → scale the database vertically (managed provider) during a quiet window.
- **Resolution:** add the missing index or rewrite the query (with a migration and a test), cap expensive reports, move analytics to the read replica/continuous aggregates when they exist.
- **Escalation:** on-call engineer; technical lead (owner) immediately if CPU stays above 95 % or connections are refused; managed-database provider support if the host itself misbehaves.

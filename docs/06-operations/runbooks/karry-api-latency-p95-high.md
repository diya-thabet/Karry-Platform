---
title: "Runbook: KarryApiLatencyP95High"
area: operations
tags: [runbook, alert, api, latency, performance]
source: "P1.4, US-P1-OPS-02 AC3"
status: baseline
last_updated: 2026-10-23
---

# KarryApiLatencyP95High

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** the **95th percentile** of `karry-api` request duration is above **1 s** for **10 minutes** (severity `page`; health probes excluded; minimum 0.1 requests/s).
- **Impact:** the portal feels slow; field sync batches take longer (more battery and data on tablets); timeouts can turn into 5xx and trigger [KarryApiErrorRateHigh](karry-api-error-rate-high.md).
- **Diagnosis:**
  1. **Karry · API (RED per module)** → *p95 latency by route (top 10)*: one route or all routes?
  2. One route: open a slow trace in Tempo (Explore → `duration > 1s`, `service.name = karry-api`). Usually a slow `Npgsql` span: copy its `db.query.text` and run `EXPLAIN (ANALYZE, BUFFERS)` on a replica or staging copy (never with production personal data on a laptop).
  3. All routes: **Karry · Platform** → *DB command p95*, *DB connections by state* (pool saturated: `used` = `max`), *Process CPU*, *Working set and GC heap*.
  4. Traffic spike? *Request rate by module* (shift change, bulk import, a misbehaving client).
- **Mitigation:**
  - Pool saturated or DB slow → reduce load (pause a bulk import, delay non-urgent jobs) and see [KarryDatabaseCpuHigh](karry-database-cpu-high.md).
  - CPU-bound API → scale out the API (P1.17), then investigate.
  - A new release introduced the slowdown → roll back.
- **Resolution:** missing index, N+1 query or unbounded query found → fix with a test and, for queries, a migration; add a performance test if the path is critical (shift-change sync, weighbridge tickets).
- **Escalation:** on-call engineer; technical lead (owner) after 1 hour or if errors follow.

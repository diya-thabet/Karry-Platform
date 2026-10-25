---
title: "Runbook: KarryUsageSnapshotStale"
area: operations
tags: [runbook, alert, billing, usage, workers]
source: "P1.10, US-P1-BIL-04 AC1, ADR-0023"
status: baseline
last_updated: 2026-10-25
---

# KarryUsageSnapshotStale

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** no daily usage snapshot in which every tenant succeeded for **26 hours**, or the worker reports no such time at all for 30 minutes (`karry_billing_usage_last_success_seconds`; severity `ticket`).
- **Impact:** the usage history ("Plan & usage", `snapshotOn`) and the over-limit notice ([KarryTenantOverLimit](karry-tenant-over-limit.md)) stop. Limits are still enforced: the guard counts live at each creation.
- **Diagnosis:**
  1. Is `karry-workers` running? It takes the snapshot daily at 02:30 UTC and once at start-up.
  2. Worker logs, event **5003** (`Usage snapshot failed for tenant {Tenant}`): one tenant failing keeps the others going but makes the run incomplete. Usual causes: database unreachable, a module's usage source throwing.
  3. Run it by hand: `Karry.Workers snapshot-usage` prints tenants, metrics over their limit and failures (exit 1 if a tenant failed).
- **Mitigation:** fix the cause and run `Karry.Workers snapshot-usage`; a run on the same day replaces that day's values, so it is safe to repeat.
- **Resolution:** a complete run moves the last-success time forward and the alert clears.
- **Escalation:** ticket for the platform engineer within the working day.

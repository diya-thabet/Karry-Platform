---
title: "Runbooks"
area: operations
tags: [runbooks, alerts, incidents, on-call]
source: "created 2026-10-21"
status: baseline
last_updated: 2026-10-29
---

# Runbooks

> [Docs index](../../README.md) › [06-operations](../README.md)

One file per alert or operational procedure, named after the alert (`kebab-case.md`), using the runbook template in [llm_guidelines.md §26.6](../../00-llm/llm_guidelines.md#266-runbook--docs06-operationsrunbooksalert-namemd). Every alert created in code must have a runbook here (Phase 1 onward).

Alert rules live in [`deploy/observability/alerts/karry-alerts.yaml`](../../../deploy/observability/alerts/karry-alerts.yaml) (tested with promtool, TC-P1-OPS-006); each rule's `runbook_url` points here.

| Runbook | Alert / procedure | Severity |
|---|---|---|
| [karry-api-error-rate-high](karry-api-error-rate-high.md) | API 5xx > 2 % for 5 min | page |
| [karry-api-latency-p95-high](karry-api-latency-p95-high.md) | API p95 > 1 s for 10 min | page |
| [karry-outbox-lag-high](karry-outbox-lag-high.md) | Oldest undelivered event > 5 min | page |
| [karry-ledger-anchoring-stale](karry-ledger-anchoring-stale.md) | No full ledger anchoring run for 2 h (or worker silent) | page |
| [karry-database-cpu-high](karry-database-cpu-high.md) | Database CPU > 80 % for 15 min | page |
| [karry-certificate-expiring-soon](karry-certificate-expiring-soon.md) | TLS certificate expires in < 14 days | ticket |
| [karry-tenant-over-limit](karry-tenant-over-limit.md) | A company above a plan limit at the last usage snapshot | ticket |
| [karry-usage-snapshot-stale](karry-usage-snapshot-stale.md) | No complete daily usage snapshot for 26 h (or worker silent) | ticket |
| [karry-shift-instances-stale](karry-shift-instances-stale.md) | No complete daily shift generation for 26 h (or worker silent) | ticket |
| [karry-break-glass-support](karry-break-glass-support.md) | A Karry operator opened a support session without the company's code | page |

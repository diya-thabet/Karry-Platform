---
title: "Runbook: KarryApiErrorRateHigh"
area: operations
tags: [runbook, alert, api, errors]
source: "P1.4, US-P1-OPS-02 AC3"
status: baseline
last_updated: 2026-10-23
---

# KarryApiErrorRateHigh

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** more than **2 %** of `karry-api` requests return **5xx** for **5 minutes** (rule `deploy/observability/alerts/karry-alerts.yaml`, severity `page`). Health probes are excluded; the alert needs at least 0.1 requests/s, so one failure on a quiet night does not page.
- **Impact:** users see "An unexpected error occurred" with a trace ID. Office users cannot save; the field app keeps working offline and retries later, so no field data is lost (invariant: offline never loses data), but sync is delayed.
- **Diagnosis:**
  1. Grafana → **Karry · API (RED per module)**: which `module` and which route (p95/route and status panels) carry the 5xx.
  2. Grafana → Explore → Tempo: search `status = error` for `service.name = karry-api`; open a trace. The failing span (often `Npgsql`) shows the operation and `error.type`.
  3. Grafana → Explore → Loki: `{service_name="karry-api"} |= "Unhandled exception"`; the log line carries the same `trace_id`. A user who quotes a trace ID from an error dialog can be looked up directly (`X-Correlation-Id` = trace ID).
  4. Recent change? Compare the start of the errors with the last deployment (release notes, `service_version` label).
  5. Dependencies: `/health/ready` of the API, **Karry · Platform** DB connections (pool at maximum?), Keycloak availability.
- **Mitigation:**
  - Errors started with a deployment → **roll back** to the previous release (P1.17 procedure).
  - Database unreachable or pool exhausted → see [KarryDatabaseCpuHigh](karry-database-cpu-high.md); restart one API instance at a time only if connections are stuck.
  - One module only and a feature flag exists for the feature → switch the flag off.
- **Resolution:** fix the root cause with a test that reproduces it (guidelines §8), add the failure mode to this runbook if it is new.
- **Escalation:** on-call engineer; after 30 minutes without mitigation, the technical lead (owner). Inform affected tenants if office users are blocked for more than 30 minutes.

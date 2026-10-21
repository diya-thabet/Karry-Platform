---
title: "Infrastructure, DevOps & Observability"
area: operations
tags: [infra, ci-cd, observability, backup, dr]
source: "MASTER_PLAN.md §19"
status: baseline
last_updated: 2026-10-21
---

# Infrastructure, DevOps & Observability

> [Docs index](../README.md) › [06-operations](README.md)

## 19.1 Environments

| Env | Purpose | Data |
|---|---|---|
| `local` | Dev via Docker Compose (Postgres+extensions, Keycloak, MinIO, Redis, Mailpit) / .NET Aspire for orchestration | Seeded synthetic |
| `dev` | Integration of main branch, auto-deploy | Synthetic |
| `staging` | Release candidates, UAT, load tests | Anonymised copies |
| `pilot` / `prod` | Customers | Real |

## 19.2 Pipeline

```
PR → lint/format → build → unit + property tests → architecture tests → integration (Testcontainers)
   → SAST (CodeQL) + dependency & container scan (Trivy) + secret scan
   → preview environment (optional) → review (2 approvals for Trust/Security modules)
main → build images (SBOM, signed with cosign) → deploy dev → E2E (Playwright) → tag → staging → manual gate → prod (progressive rollout)
```

- Trunk-based development, short-lived branches, feature flags (OpenFeature) for incomplete features.
- Database migrations run as a separate job before app rollout (expand/contract).
- PWA versioning: service worker update prompts at safe points (never mid-form); server supports **N-2 client versions** because offline devices update late.

## 19.3 Observability

- OpenTelemetry traces from PWA (sync calls) → API → DB; correlation ID shown in UI error messages for support.
- Metrics: RED (rate/errors/duration) per endpoint, sync backlog per device, outbox lag, ledger anchoring lag, AI tokens/cost per tenant.
- Business health alerts: "site X has not synced in 24 h", "pending reviews > SLA", "anchoring failed".
- Logs structured (Serilog → Loki), PII-scrubbed.

## 19.4 Backup & DR

- PostgreSQL PITR (WAL archiving) with ≥ 35-day retention; daily logical backups for tenant-level export/restore.
- Object storage versioning + cross-region replication for prod.
- Quarterly restore drill with documented RTO/RPO results; ledger verification run on restored copy.

## 19.5 Cost posture

- Start on a single managed Kubernetes cluster (or a container PaaS) + managed PostgreSQL; scale vertically first.
- Target infra cost per standard tenant tracked from pilot onward (unit economics for SaaS pricing).

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
| `local` | Dev via Docker Compose (Postgres+extensions, Keycloak, SeaweedFS S3 with Object Lock, Valkey, Mailpit, Grafana LGTM); .NET Aspire optional ([ADR-0017](../adr/0017-local-stack-and-object-storage.md)) | Seeded synthetic |
| `dev` | Integration of main branch, auto-deploy | Synthetic |
| `staging` | Release candidates, UAT, load tests | Anonymised copies |
| `pilot` / `prod` | Customers | Real |

## 19.2 Pipeline

```
PR → lint/format → build → unit + property tests → architecture tests → integration (Testcontainers)
   → SAST (Semgrep CE, [ADR-0021](../adr/0021-security-scanning-private-repo.md)) + dependency & container scan (Trivy) + secret scan
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
- Logs structured (`Microsoft.Extensions.Logging` with source-generated `LoggerMessage` → OpenTelemetry → Loki), no personal data: the request-start log with the full URL is not exported, log scopes are off (records carry trace and span IDs).
- **Implemented in P1.4** (`Karry.BuildingBlocks/Observability`): OpenTelemetry in `karry-api` and `karry-workers` (traces: ASP.NET Core, HttpClient, Npgsql, outbox dispatch; metrics: HTTP RED with a `module` tag, runtime, Npgsql pool, `karry.outbox.messages`, `karry.outbox.oldest_pending_age`; logs). Exported with OTLP only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set. Span attributes are **allow-listed** (`TelemetryAttributes.Allowed`): `tenant_id`, `module`, `route`, `user_role`, `event_type` plus semantic-convention attributes without data; `url.*`, user agent and client address are dropped; SQL text is kept (always parameterized). Health probes are not traced. Verified by TC-P1-OPS-003 and TC-P1-OPS-005.

## 19.4 Backup & DR

- PostgreSQL PITR (WAL archiving) with ≥ 35-day retention; daily logical backups for tenant-level export/restore.
- Object storage versioning + cross-region replication for prod.
- Quarterly restore drill with documented RTO/RPO results; ledger verification run on restored copy.

## 19.5 Cost posture

- Start on a single managed Kubernetes cluster (or a container PaaS) + managed PostgreSQL; scale vertically first.
- Target infra cost per standard tenant tracked from pilot onward (unit economics for SaaS pricing).

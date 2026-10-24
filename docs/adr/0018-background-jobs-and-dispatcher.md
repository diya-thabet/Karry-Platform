---
title: "ADR-0018: In-house outbox dispatcher and Quartz.NET for scheduled jobs"
area: adr
tags: [background-jobs, outbox, scheduling, quartz, licensing]
source: "Phase 1 sub-phase P1.0"
status: accepted
last_updated: 2026-10-24
---

# ADR-0018 — In-house outbox dispatcher and Quartz.NET for scheduled jobs

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-22
- **Deciders:** Product owner, software architect
- **Related:** ADR-0015 (refines its open choice), ADR-0008, US-P1-AUD-02, US-P1-BIL-04, US-P1-ORG-02

## Context

ADR-0015 chose a transactional outbox but left the dispatcher open ("thin in-house dispatcher or Wolverine"). The technology stack left background jobs open ("Hangfire or Quartz.NET"). Phase 1 needs: outbox dispatch, hourly ledger anchoring, daily usage snapshots, shift-instance generation; later RUL recomputation, alerts and reports.

## Options considered

### Outbox dispatcher
1. **In-house dispatcher** (chosen): a few hundred lines in `Karry.BuildingBlocks` + `Karry.Workers`; polls the outbox with `FOR UPDATE SKIP LOCKED`, ordered per aggregate, retries with back-off, records processed messages for idempotent consumers. **Pros:** no licence risk, fully understood, easy to test. **Cons:** we own the code.
2. Wolverine: feature-rich. **Cons:** large surface and conventions for a small need; extra upgrade dependency.
3. MediatR / MassTransit v9: commercial licences since 2025. Rejected unless budgeted.

### Scheduled jobs
1. **Quartz.NET** (chosen, verified current 4.x): cron triggers, clustering on PostgreSQL (`misfire` handling, one runner per job across instances), MIT/Apache licence. **Pros:** what we need, nothing more.
2. Hangfire: dashboard and fire-and-forget jobs. **Cons:** our fire-and-forget work already goes through the outbox; Pro features are paid; adds its own storage schema.

## Decision

- Outbox dispatch uses an **in-house dispatcher** hosted in `Karry.Workers` (contract and tests in P1.3, TC-P1-PLT-007;
  running there since P1.8 for Tenancy and Identity, `Karry.Workers dispatch-now` drains once). Another module's events
  are consumed through a contract of the publishing module (e.g. `ITenantProvisionedHandler` in Tenancy.Contracts),
  because modules may not reference each other's domain event types.
- Scheduled work uses **Quartz.NET** with persistent PostgreSQL job store in clustered mode, hosted in `Karry.Workers`.
- Every job is idempotent, tenant-aware (iterates tenants explicitly, sets tenant context per tenant transaction), emits metrics (last success, duration, lag) and has an alert + runbook when it guards an invariant (anchoring).

## Consequences

- ✅ No licensed dependency in the core path; small, testable building blocks.
- ⚠ We maintain the dispatcher; it is covered by integration tests with crash/restart scenarios.
- ⚠ Revisit if the number of message types or throughput grows beyond what polling handles (trigger: outbox lag SLO breached).

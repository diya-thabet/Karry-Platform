---
title: "Architecture Decision Records (ADR) Index"
area: adr
tags: [adr, decisions]
source: "MASTER_PLAN.md §24"
status: baseline
last_updated: 2026-10-21
---

# Architecture Decision Records (ADR) Index

> [Docs index](../README.md)

Each architecture decision gets **one file**: `adr/NNNN-short-title.md` (4-digit number, kebab-case), using the MADR template in [llm_guidelines.md §26.1](../00-llm/llm_guidelines.md#261-adr-madr-style--docsadrnnnn-short-titlemd). Status flow: Proposed → Accepted / Rejected → Superseded. When an ADR is written, link it in the table below.

Initial set written in Phase 0 (WP-P0-16). All are **Proposed** until the owner accepts them at the M0 gate:

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith with DDD bounded contexts | Proposed |
| [0002](0002-dotnet-10-lts.md) | Backend on .NET 10 LTS with version pinning and upgrade policy | Proposed |
| [0003](0003-postgresql-and-extensions.md) | PostgreSQL 17/18 with PostGIS, TimescaleDB and pgvector | Proposed |
| [0004](0004-multi-tenancy-rls.md) | Multi-tenancy: pooled database with Row-Level Security, silo option | Proposed |
| [0005](0005-offline-first-pwa.md) | Offline-first PWA with Dexie outbox and idempotent sync | Proposed |
| [0006](0006-immutable-facts.md) | Immutable facts and correction by reversal | Proposed |
| [0007](0007-signature-envelope.md) | Signature envelope: device ECDSA + KMS countersignature + optional RFC 3161 | Proposed |
| [0008](0008-hash-chained-ledger.md) | Hash-chained audit ledger with WORM anchoring | Proposed |
| [0009](0009-core-math-dotnet.md) | Core business math in .NET; Python sidecar only for ML and forecasting | Proposed |
| [0010](0010-material-state.md) | Material state (bank / loose / compacted) on every volume quantity | Proposed |
| [0011](0011-dscr-solvency.md) | DSCR as the asset solvency metric | Proposed |
| [0012](0012-identity-authorization.md) | Keycloak for identity; in-app RBAC + ABAC + field masking | Proposed |
| [0013](0013-ai-tool-calling.md) | AI copilot via tool-calling over a governed semantic layer, with provider abstraction | Proposed |
| [0014](0014-echarts.md) | Apache ECharts as the primary charting library | Proposed |
| [0015](0015-transactional-outbox.md) | Transactional outbox for domain and integration events | Proposed |
| [0016](0016-edge-agent.md) | Site edge agent for weighbridge and plant integration | Proposed |

Added in Phase 1 sub-phase P1.0 (version check and open choices of the stack):

| ADR | Title | Status |
|---|---|---|
| [0017](0017-local-stack-and-object-storage.md) | Local stack with Docker Compose and SeaweedFS as the self-hosted S3 store (MinIO OSS archived) | Proposed |
| [0018](0018-background-jobs-and-dispatcher.md) | In-house outbox dispatcher and Quartz.NET for scheduled jobs | Proposed |
| [0019](0019-feature-flags.md) | Feature flags with OpenFeature and a database-backed provider | Proposed |
| [0020](0020-frontend-toolchain-versions.md) | Frontend toolchain: Node 24 LTS, pnpm 12, TypeScript 6.0 until typescript-eslint supports 7 | Proposed |

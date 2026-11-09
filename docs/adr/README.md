---
title: "Architecture Decision Records (ADR) Index"
area: adr
tags: [adr, decisions]
source: "MASTER_PLAN.md §24"
status: baseline
last_updated: 2026-11-09
---

# Architecture Decision Records (ADR) Index

> [Docs index](../README.md)

Each architecture decision gets **one file**: `adr/NNNN-short-title.md` (4-digit number, kebab-case), using the MADR template in [llm_guidelines.md §26.1](../00-llm/llm_guidelines.md#261-adr-madr-style--docsadrnnnn-short-titlemd). Status flow: Proposed → Accepted / Rejected → Superseded. When an ADR is written, link it in the table below.

Initial set written in Phase 0 (WP-P0-16). **All ADRs 0001–0020 were accepted by the owner on 2026-10-22.** Changing an accepted decision requires a new ADR that supersedes it:

| ADR | Title | Status |
|---|---|---|
| [0001](0001-modular-monolith.md) | Modular monolith with DDD bounded contexts | Accepted |
| [0002](0002-dotnet-10-lts.md) | Backend on .NET 10 LTS with version pinning and upgrade policy | Accepted |
| [0003](0003-postgresql-and-extensions.md) | PostgreSQL 17/18 with PostGIS, TimescaleDB and pgvector | Accepted |
| [0004](0004-multi-tenancy-rls.md) | Multi-tenancy: pooled database with Row-Level Security, silo option | Accepted |
| [0005](0005-offline-first-pwa.md) | Offline-first PWA with Dexie outbox and idempotent sync | Accepted |
| [0006](0006-immutable-facts.md) | Immutable facts and correction by reversal | Accepted |
| [0007](0007-signature-envelope.md) | Signature envelope: device ECDSA + KMS countersignature + optional RFC 3161 | Accepted |
| [0008](0008-hash-chained-ledger.md) | Hash-chained audit ledger with WORM anchoring | Accepted |
| [0009](0009-core-math-dotnet.md) | Core business math in .NET; Python sidecar only for ML and forecasting | Accepted |
| [0010](0010-material-state.md) | Material state (bank / loose / compacted) on every volume quantity | Accepted |
| [0011](0011-dscr-solvency.md) | DSCR as the asset solvency metric | Accepted |
| [0012](0012-identity-authorization.md) | Keycloak for identity; in-app RBAC + ABAC + field masking | Accepted |
| [0013](0013-ai-tool-calling.md) | AI copilot via tool-calling over a governed semantic layer, with provider abstraction | Accepted |
| [0014](0014-echarts.md) | Apache ECharts as the primary charting library | Accepted |
| [0015](0015-transactional-outbox.md) | Transactional outbox for domain and integration events | Accepted |
| [0016](0016-edge-agent.md) | Site edge agent for weighbridge and plant integration | Accepted |

Added in Phase 1 sub-phase P1.0 (version check and open choices of the stack):

| ADR | Title | Status |
|---|---|---|
| [0017](0017-local-stack-and-object-storage.md) | Local stack with Docker Compose and SeaweedFS as the self-hosted S3 store (MinIO OSS archived) | Accepted |
| [0018](0018-background-jobs-and-dispatcher.md) | In-house outbox dispatcher and Quartz.NET for scheduled jobs | Accepted |
| [0019](0019-feature-flags.md) | Feature flags with OpenFeature and a database-backed provider | Accepted |
| [0020](0020-frontend-toolchain-versions.md) | Frontend toolchain: Node 24 LTS, pnpm 12, TypeScript 6.0 until typescript-eslint supports 7 | Accepted |
| [0021](0021-security-scanning-private-repo.md) | Security scanning with gitleaks, Trivy and Semgrep CE (CodeQL needs a paid licence on private repos) | Accepted |

Added in Phase 1 sub-phase P1.9 (authorization):

| ADR | Title | Status |
|---|---|---|
| [0022](0022-authorization-engine.md) | Authorization engine: permissions declared in code, scopes per assignment, masking in the serializer | Accepted |
| [0023](0023-plans-subscriptions-entitlements.md) | Plans as versioned data, one subscription per tenant, entitlements resolved in-process | Accepted |

Added in Phase 1 sub-phase P1.11 (organization):

| ADR | Title | Status |
|---|---|---|
| [0024](0024-sites-geometry-and-shift-time.md) | Sites as PostGIS geometry, shift time computed with NodaTime in the site's time zone | Accepted |

Added in Phase 1 sub-phase P1.12 (materials and units):

| ADR | Title | Status |
|---|---|---|
| [0025](0025-units-engine-and-materials.md) | Units engine in the shared kernel, materials in their own module, swell derived from densities | Accepted |

Added in Phase 1 sub-phase P1.13 (assets and meters):

| ADR | Title | Status |
|---|---|---|
| [0026](0026-asset-registry-meters-and-labels.md) | Asset registry with a type catalogue in code, append-only site and meter history, random label tokens | Accepted |

Added in Phase 1 sub-phase P1.14 (Excel import):

| ADR | Title | Status |
|---|---|---|
| [0027](0027-excel-import.md) | Excel import as a server-side job: one kind per file, targets owned by their modules, all-or-nothing commit | Accepted |
| [0028](0028-field-devices-offline-data-shared-tablet.md) | Field tablets as enrolled devices: device-signed requests, delta reference data, offline PIN unlock | Accepted |
| [0029](0029-operator-console-support-access-audit-viewer.md) | Operator console, consented read-only support sessions, audit viewer with subject index and masked export | Accepted |

Added after P1.16 (owner request, 2026-10-29):

| ADR | Title | Status |
|---|---|---|
| [0030](0030-arabic-and-malinke-languages.md) | Arabic and Malinké (Latin and N'Ko scripts) in the apps: right to left, bundled fonts, N'Ko spelled from Latin, drafts for native review | Accepted |

Added in Phase 2 sub-phase P2.0 (they shape P2.1–P2.3; proposed, accepted when their sub-phase is done):

| ADR | Title | Status |
|---|---|---|
| [0031](0031-sync-command-processing.md) | Sync command processing: handlers in the upload request, one savepoint per command, business problems stored and flagged | Accepted (built in P2.1) |
| [0032](0032-evidence-blobs.md) | Evidence blobs: content-addressed, pre-signed PUT with a checksum the store verifies, Object Lock retention, signed capture record instead of EXIF | Accepted |
| [0033](0033-signature-envelopes-and-server-keys.md) | Signature envelopes v1 and server keys: OpenBao transit behind `IServerSigner`, asynchronous countersignature, rotation by `kid` | Proposed |

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

ADRs live in `docs/adr/NNNN-title.md` (MADR format). Initial set to write in Phase 0:

| ADR | Title | Status |
|---|---|---|
| 0001 | Modular monolith with DDD bounded contexts | Proposed |
| 0002 | .NET 10 LTS, version pinning & upgrade policy | Proposed |
| 0003 | PostgreSQL 17/18 + PostGIS + TimescaleDB + pgvector | Proposed |
| 0004 | Multi-tenancy: pool model with RLS, silo option | Proposed |
| 0005 | Offline-first PWA with Dexie outbox and idempotent sync | Proposed |
| 0006 | Immutable facts & correction-by-reversal | Proposed |
| 0007 | Signature envelope: device ECDSA + KMS countersignature + RFC 3161 | Proposed |
| 0008 | Hash-chained audit ledger with WORM anchoring | Proposed |
| 0009 | Core math in .NET; Python sidecar for ML/forecasting only | Proposed |
| 0010 | Material state (bank/loose/compacted) in all volume quantities | Proposed |
| 0011 | DSCR as solvency metric (replaces codex formula) | Proposed |
| 0012 | Keycloak for identity; in-app RBAC/ABAC + field masking | Proposed |
| 0013 | AI via tool-calling over semantic layer; provider abstraction | Proposed |
| 0014 | Apache ECharts as primary charting library | Proposed |
| 0015 | Transactional outbox for domain/integration events | Proposed |
| 0016 | Edge agent for weighbridge & plant integration | Proposed (P6) |

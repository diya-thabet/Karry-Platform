---
title: "ADR-0001: Modular monolith with DDD bounded contexts"
area: adr
tags: [architecture, monolith, microservices, modules]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0001 — Modular monolith with DDD bounded contexts

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** US-P1-PLT-01; [modular-monolith-decision.md](../02-architecture/modular-monolith-decision.md)

## Context

Karry is built by a team of 5–7 engineers and must reach paying customers quickly. Its core promise, every business change committed together with a signed, hash-chained ledger entry, is transactional. Field devices upload batches that span several domains (shifts, fuel, tickets, signatures). Domain boundaries are not yet validated by event storming with real operators.

## Options considered

1. Microservices from day 1 — **Pros:** Independent deploy/scale per service. **Cons:** Distributed transactions for the ledger; per-service infra, pipelines, on-call; ~30–50 % delivery overhead; boundaries expensive to move; higher cost per tenant.
2. Unstructured monolith — **Pros:** Fastest start. **Cons:** Boundaries erode; extraction later becomes a rewrite.
3. **Modular monolith** (chosen) — **Pros:** One transaction for business change + ledger + outbox; one consistent sync endpoint; cheap boundary refactoring; low infra cost. **Cons:** Requires discipline: enforced module boundaries.

## Decision

Build one .NET solution split into bounded-context modules (`Domain`, `Application`, `Infrastructure`, `Api`, `Contracts` per module), each owning a PostgreSQL schema. Modules interact only through public contracts (in-process) and integration events via the transactional outbox (ADR-0015). Deployables: API host, Worker host (same code), Python analytics sidecar (ADR-0009), Edge agent (ADR-0016), plus Keycloak (ADR-0012). Architecture tests in CI block boundary violations. A module is extracted into a service only when a measured trigger fires (throughput, security isolation, team topology, availability, scaling economics) and a new ADR is accepted.

## Consequences

- ✅ Fastest path to revenue; transactional trust guarantees stay simple; offline sync has one coherent server.
- ✅ Extraction stays mechanical: schema per module, contracts, CloudEvents envelopes, module-prefixed routes.
- ⚠ Needs boundary enforcement from day 1 (US-P1-PLT-01 AC2) and per-module telemetry to measure extraction triggers.
- 🔎 We were wrong if: a module's load or failure profile measurably harms the core SLO, or ≥ 3 squads block each other on releases.

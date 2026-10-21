---
title: "Architecture Principles"
area: architecture
tags: [principles]
source: "MASTER_PLAN.md §6"
status: baseline
last_updated: 2026-10-21
---

# Architecture Principles

> [Docs index](../README.md) › [02-architecture](README.md)

1. **Trust is the product.** Any feature that weakens the evidence chain (silent edits, unsigned overrides, deletes) is rejected.
2. **Offline is the default, online is the optimisation.** Field workflows are designed against the local store first.
3. **Append, don't overwrite.** Corrections are new facts referencing old ones (reversal + re-entry), never in-place mutation of signed data.
4. **One source of truth per rule.** Each business formula is implemented once, in the domain layer, and versioned.
5. **Modular monolith first.** Clear bounded contexts with internal APIs; extract a service only with a measured reason (scale, team, isolation).
6. **Configuration over code for topology.** Plants, machines, wear parts, units and roles are data, not deployments.
7. **Explainable numbers.** Every KPI and AI answer must be traceable to its inputs ("show calculation").
8. **Security and tenancy in depth.** Tenant isolation at the database (RLS), application (query filters), and API (claims) layers.
9. **Boring, supported technology.** LTS runtimes, mainstream libraries, permissive or budgeted licenses.
10. **Measure everything.** OpenTelemetry from day one; product analytics for adoption.

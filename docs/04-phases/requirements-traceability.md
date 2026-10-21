---
title: "Requirements Traceability Matrix"
area: phases
tags: [traceability, requirements]
source: "MASTER_PLAN.md §25"
status: baseline
last_updated: 2026-10-21
---

# Requirements Traceability Matrix

> [Docs index](../README.md) › [04-phases](README.md)

| Codex requirement | Module(s) | Section | Phase |
|---|---|---|---|
| Field data fraud & unverified fuel logging | Field Ops, Fuel, Verification, Trust | [§10.6](../03-domain/computational-engines.md#106-fuel-analytics-engine), [§13](../02-architecture/trust-layer-signatures-audit.md) | P2–P3 |
| Hybrid maintenance metrics (H / KM / Calendar fallback) | Fleet & Assets, Maintenance | [§10.5](../03-domain/computational-engines.md#105-hybrid-maintenance--rul-engine) | P2 (data), P4 |
| Dynamic conveyor speed degradation | Plant Topology, Analytics | [§10.4](../03-domain/computational-engines.md#104-conveyor-capacity-engine) | P6–P7 |
| Wear component failure prediction | Maintenance, Procurement | [§10.5](../03-domain/computational-engines.md#105-hybrid-maintenance--rul-engine) | P4 |
| Closed-loop mass yield | Plant Topology | [§10.2](../03-domain/computational-engines.md#102-blast--yield-engine)–10.3 | P6 |
| Paper bureaucracy → e-signatures | Signatures & Audit | [§13.2](../02-architecture/trust-layer-signatures-audit.md#132-signature-envelope) | P2 |
| Inter-quarry warehouse leakage | Inventory & Warehouses | [§9.4](../03-domain/bounded-contexts.md#94-asset-lifetime-history-inter-site-transfers), [§10.9](../03-domain/computational-engines.md#109-inventory-variance-engine) | P5 |
| Dynamic unit toggle (m³ ↔ t) | Units engine (SharedKernel) | [§10.1](../03-domain/computational-engines.md#101-units--conversion-engine) | P1 |
| Loan amortization vs weather risk | Asset Finance, Weather | [§10.7](../03-domain/computational-engines.md#107-asset-cost--finance-engine)–10.8 | P7 |
| Two-tier controller approval + audit trail | Verification, Trust | [§13.1](../02-architecture/trust-layer-signatures-audit.md#131-generic-verification-workflow) | P2 |
| Inter-shift ΔH continuity | Field Ops | [§3.1](../03-domain/spec-review-corrections.md#31-mathematical--physical-model-corrections) M9, [§13](../02-architecture/trust-layer-signatures-audit.md) | P2 |
| Weighbridge ticket security | Weighbridge | [§13.4](../02-architecture/trust-layer-signatures-audit.md#134-scale-ticket-rules) | P3 |
| Granular RBAC with masking | Tenancy & Identity | [§14.2](../02-architecture/security-identity-tenancy.md#142-authorization-model) | P1 |
| Semi-annual/annual stock audits | Inventory | [§10.9](../03-domain/computational-engines.md#109-inventory-variance-engine) | P5 |
| AI copilot (NL Q&A, fuel, grouping, bottleneck, weather advisory) | AI Copilot + engines | [§15](../02-architecture/ai-copilot-architecture.md) | P8 |
| Visual analytics (courbes et graphiques) | Analytics | [§16](../02-architecture/visual-analytics.md) | P3 (v1) → P7 |
| Offline-first PWA | Client platform | [§12](../02-architecture/offline-sync.md) | P1–P2 |
| Multi-tenant RLS, i18n, multi-currency | Platform | [§14.4](../02-architecture/security-identity-tenancy.md#144-multi-tenancy-implementation), [§5](../02-architecture/non-functional-requirements.md) | P1, P7 |
| Plug-and-play machinery topology | Plant Topology | [§10.3](../03-domain/computational-engines.md#103-plant-graph--mass-balance-engine) | P6 |

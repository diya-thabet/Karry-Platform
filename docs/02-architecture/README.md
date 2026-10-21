---
title: "Architecture"
area: architecture
tags: [architecture, stack, security, data, offline, ai, api]
source: "created 2026-10-21"
status: baseline
last_updated: 2026-10-21
---

# 02-architecture — System Architecture

> [Docs index](../README.md)

How Karry is built. Business rules and formulas live in [03-domain](../03-domain/README.md); decisions are recorded as [ADRs](../adr/README.md).

| File | Purpose |
|---|---|
| [overview.md](overview.md) | Purpose of the architecture docs, executive summary, key decisions table, timeline reality |
| [architecture-principles.md](architecture-principles.md) | The 10 principles every design must respect |
| [modular-monolith-decision.md](modular-monolith-decision.md) | Detailed reasoning: modular monolith vs microservices, what is separate from day 1, extraction triggers, multi-tenancy models |
| [non-functional-requirements.md](non-functional-requirements.md) | Offline, devices, availability, performance, scale, security, privacy, RPO/RTO, i18n, accessibility targets |
| [technology-stack.md](technology-stack.md) | Every layer's technology with rationale and alternatives; licence warnings |
| [system-architecture.md](system-architecture.md) | C4 context & container diagrams, module layering, field-entry-to-ledger sequence |
| [data-architecture.md](data-architecture.md) | Storage layout, conventions (UUIDv7, UTC, money, quantities), core tables, read models, retention |
| [offline-sync.md](offline-sync.md) | Client data layers, sync protocol, conflict policy, time integrity, device management, storage quota |
| [trust-layer-signatures-audit.md](trust-layer-signatures-audit.md) | Verification workflow, signature envelope (device ECDSA + KMS), hash-chained ledger, scale ticket rules |
| [security-identity-tenancy.md](security-identity-tenancy.md) | Identity, RBAC/ABAC/masking, STRIDE threats, RLS implementation, data protection |
| [ai-copilot-architecture.md](ai-copilot-architecture.md) | Tool-calling over a semantic layer, capabilities by phase, provider strategy, evaluations |
| [visual-analytics.md](visual-analytics.md) | Dashboards catalogue and charting engineering rules |
| [integrations-and-edge.md](integrations-and-edge.md) | Site edge agent, telematics (AEMP 2.0), OPC UA, ERP, notifications, weather |
| [api-standards.md](api-standards.md) | REST conventions, Problem Details, idempotency, pagination, versioning, units in API |
| [repository-structure.md](repository-structure.md) | Target folder layout of the monorepo (backend, frontend, analytics, tests, deploy, tools, docs) |
| [threat-model.md](threat-model.md) | STRIDE threat model v1: actors, trust boundaries, 17 threats mapped to mitigations, stories and tests |

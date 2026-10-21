---
title: "Risk Register"
area: management
tags: [risks]
source: "MASTER_PLAN.md §23"
status: baseline
last_updated: 2026-10-21
---

# Risk Register

> [Docs index](../README.md) › [07-management](README.md)

| # | Risk | Prob. | Impact | Mitigation | Owner |
|---|---|---|---|---|---|
| R1 | Field user adoption fails (complexity, literacy, gloves, sun glare) | High | Critical | Prototype tests on site, icon-first UI, outdoor mode, minimal typing, local-language training videos | PO / UX |
| R2 | Offline sync bugs cause data loss → trust collapse | Med | Critical | Immutable facts, idempotency, extensive offline E2E, device lab, sync telemetry | Tech Lead |
| R3 | Domain formulas wrong / not accepted by engineers | Med | High | Phase 0 expert validation, golden datasets, versioned formulas, "show calculation" | Architect |
| R4 | E-signatures not legally recognised in a market | Med | High | Legal review per country; TSA timestamps; QTSP integration path | Founder / Legal |
| R5 | Scope creep toward ERP | High | High | Explicit out-of-scope list, exports instead of features, ADR for scope changes | PO |
| R6 | Collusion between operator and controller defeats two-tier review | Med | High | SoD, four-eyes, statistical anomaly detection independent of approvals, telematics cross-check | Architect |
| R7 | Low-end devices too slow / storage evicted | Med | High | Performance budgets, persistent storage request, photo compression, device minimum spec | FE Lead |
| R8 | Weighbridge hardware heterogeneity | High | Med | Edge agent with protocol plugins, manual fallback with flags | BE Lead |
| R9 | LLM cost, hallucinations or data-residency objections | Med | Med | Tool-based numbers, eval harness, budgets, sovereign model option, per-tenant opt-out | Architect |
| R10 | Tenant data leak | Low | Critical | RLS + filters + automated isolation tests + pen test | Security |
| R11 | Key-person dependency (small team) | Med | High | ADRs, docs-as-code, pairing, runbooks | Tech Lead |
| R12 | Library licence changes (e.g., MediatR/AutoMapper/MassTransit went commercial) | Med | Low | Prefer BCL/in-house thin abstractions, licence check in CI | Tech Lead |
| R13 | Connectivity at sites worse than assumed (no sync for days) | Med | Med | 72 h+ offline target, SMS/WhatsApp alert channel, edge agent as local relay | Architect |

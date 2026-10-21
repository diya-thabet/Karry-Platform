---
title: "Architecture Overview & Executive Summary"
area: architecture
tags: [overview, summary, decisions, timeline]
source: "MASTER_PLAN.md §1; MASTER_PLAN.md §2"
status: baseline
last_updated: 2026-10-21
---

# Architecture Overview & Executive Summary

> [Docs index](../README.md) › [02-architecture](README.md)

## Purpose of this Document

This document is the **single architectural source of truth** for building Karry. It:

- Translates the business/technical codex into an **implementable system design**.
- **Challenges and corrects** the specification where the mathematics, terminology, or timeline would cause defects, legal exposure, or delivery failure.
- Fixes the **technology stack** with explicit rationale and alternatives.
- Defines **every delivery phase** — scope, deliverables, exit criteria — from discovery to general availability.
- Establishes the **engineering standards** (APIs, testing, security, DevOps) the team is held to.

Changes to this document after baseline go through an ADR (see [§24](../adr/README.md)). Each major decision gets its own file in `docs/adr/`.

---

## Executive Summary

Karry is a vertical SaaS operating system for quarries, crushing plants, sand-washing plants and open-pit mines. Its commercial value rests on three pillars:

1. **Trusted field data** — every meter reading, fuel dispatch, shift and scale ticket is captured offline on low-cost Android tablets, verified by a controller, signed, and written to a tamper-evident ledger. This attacks the #1 cost leak in the industry: **fuel theft and falsified shift reports**.
2. **Operational intelligence** — a configurable plant graph (crushers, screens, conveyors) computes mass balance and yield; a hybrid maintenance engine (hours / km / calendar) predicts wear-part failure early enough to order parts with long lead times.
3. **Financial truth per machine** — each asset's cost per hour, net margin, and debt-service coverage are visible, adjusted for weather-driven stoppages.

### Key architectural decisions (summary)

| Decision | Choice |
|---|---|
| Architecture style | **Modular monolith** (DDD bounded contexts), extractable to services later |
| Backend | **.NET 10 (LTS)**, ASP.NET Core, EF Core 10 |
| Analytics / ML sidecar | **Python 3.13 + FastAPI + NumPy/SciPy/pandas** — *only* for forecasting & anomaly models, not for core business rules |
| Database | **PostgreSQL 17/18** + PostGIS + TimescaleDB, Row-Level Security for tenancy |
| Frontend | **React 19 + TypeScript + Vite**, Tailwind CSS v4, shadcn/ui, TanStack Query/Router |
| Offline | Service Worker (Workbox), **Dexie.js (IndexedDB)**, outbox-based sync with idempotency |
| Charts | **Apache ECharts** (high-volume time-series, heatmaps) + Recharts for simple widgets |
| Identity | **Keycloak** (OIDC, MFA, per-tenant realms or organizations) |
| Signatures | Real asymmetric cryptography (ECDSA P-256 via WebCrypto + server KMS), hash-chained audit ledger, RFC 3161 timestamps |
| AI | Provider-agnostic AI gateway; **tool-calling over a governed semantic layer** (no free-form text-to-SQL); Claude as default provider, self-hosted open-weight model option for data-sovereign tenants |
| Infra | Containers, OpenTofu, GitHub Actions, Kubernetes (managed) from GA; OpenTelemetry + Grafana stack |

### Timeline reality

The codex proposes **8 weeks** for the full system. That is not achievable for this scope at professional quality. This plan delivers:

- a **pilot-ready MVP in ~16 weeks** (field capture, verification, signatures, weighbridge tickets, basic maintenance),
- **commercial v1 in ~9–10 months**,
- **full codex scope (plant graph, AI copilot, finance, SaaS self-service) in ~12–14 months**,

assuming a core team of 5–7 engineers (see [§22](../07-management/team-and-governance.md)). Phases are ordered so that **revenue-generating value (anti-fraud) ships first**.

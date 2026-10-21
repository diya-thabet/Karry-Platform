---
title: "ADR-0009: Core business math in .NET; Python sidecar only for ML and forecasting"
area: adr
tags: [formulas, python, dotnet, analytics]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0009 — Core business math in .NET; Python sidecar only for ML and forecasting

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** [computational-engines.md](../03-domain/computational-engines.md); US-P7-ANA-01

## Context

The specification placed formula computation in a separate Python service. Business rules implemented in two runtimes drift apart and double the testing surface.

## Options considered

1. All math in Python — **Pros:** NumPy ecosystem. **Cons:** Business rules split from the domain; cross-service calls in transactions.
2. All analytics in .NET — **Pros:** One runtime. **Cons:** Weaker forecasting/statistics ecosystem.
3. **Deterministic business formulas in .NET; Python for ML/forecasting** (chosen) — **Pros:** One source of truth for rules; best tools for statistics. **Cons:** Two runtimes to operate (sidecar is stateless).

## Decision

Units, blast/yield, mass balance, conveyor capacity, RUL, fuel baselines (v1), cost/DSCR, weather capacity and inventory variance are pure, versioned .NET domain services with property-based tests. The Python FastAPI sidecar (from Phase 7) hosts seasonal forecasting, anomaly models, conveyor degradation regression and Weibull fitting. It is stateless, receives data from or reads a replica for the .NET workers, returns results with a model version, and never writes business tables.

## Consequences

- ✅ Each business rule exists once; formulas are reproducible by version.
- ⚠ Contract tests are needed between the .NET client and the Python API.
- 🔎 Revisit if a formula needs heavy numerical libraries unavailable in .NET (MathNet.Numerics covers sparse linear algebra for mass balance).

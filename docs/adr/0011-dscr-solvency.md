---
title: "ADR-0011: DSCR as the asset solvency metric"
area: adr
tags: [finance, dscr, loans, formulas]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0011 — DSCR as the asset solvency metric

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** [spec-review-corrections.md](../03-domain/spec-review-corrections.md) M7; US-P7-FIN-08

## Context

The specification's solvency inequality compared an annual net revenue sum with annual debt service divided by the loan term (dimensionally wrong) and counted depreciation as a cost while the loan already finances the asset.

## Options considered

1. Codex formula — **Pros:** Matches spec. **Cons:** Mathematically wrong; misleading to owners and banks.
2. **Debt Service Coverage Ratio** (chosen) — **Pros:** Bank-standard; understood by lenders. **Cons:** Requires a revenue attribution policy per asset.

## Decision

Solvency is `DSCR = (attributed revenue − cash OpEx) / (principal + interest)` for the same period, computed monthly and trailing-12-months per asset and portfolio. Depreciation is excluded from the numerator and reported separately in TCO. Threshold is configurable (default 1.25); statuses Healthy / Watch / Breach. Revenue attribution policy is chosen by the tenant and shown with every result.

## Consequences

- ✅ Speaks the bank's language and supports the "bank-ready asset report" selling point.
- ⚠ Attribution policy choices must be transparent and versioned.

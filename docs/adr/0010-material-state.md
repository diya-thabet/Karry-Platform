---
title: "ADR-0010: Material state (bank / loose / compacted) on every volume quantity"
area: adr
tags: [units, volume, density, swell-factor]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0010 — Material state (bank / loose / compacted) on every volume quantity

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** [spec-review-corrections.md](../03-domain/spec-review-corrections.md) M2; US-P1-UNI-01

## Context

The specification's m³ ↔ tonnes toggle ignored that a cubic metre in the bench, in a truck and in a compacted fill are different quantities. Ignoring the swell factor misprices loads by 30–60 %.

## Options considered

1. Single density per material — **Pros:** Simple. **Cons:** Wrong invoices and production figures.
2. **Quantity value object with unit + material state; densities per state, versioned** (chosen) — **Pros:** Correct conversions; explainable. **Cons:** Slightly more data entry at onboarding.

## Decision

All quantities are `Quantity(value, unit, state?)`; volumes carry `BANK`, `LOOSE` or `COMPACTED`. Densities, swell factors and moisture defaults are configured per material and state with effective dates; conversions use the version valid at the event time. Arithmetic across incompatible units or states is rejected. Weighbridge mass is authoritative for invoicing; volumes are derived from mass.

## Consequences

- ✅ Correct billing and production reconciliation; conversions explainable in the UI.
- ⚠ Onboarding must collect densities per state (import template provides defaults).

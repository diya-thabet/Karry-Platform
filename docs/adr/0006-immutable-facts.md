---
title: "ADR-0006: Immutable facts and correction by reversal"
area: adr
tags: [immutability, audit, corrections, trust]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0006 — Immutable facts and correction by reversal

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** [trust-layer-signatures-audit.md](../02-architecture/trust-layer-signatures-audit.md); US-P2-VER-04, US-P3-WB-02

## Context

Karry sells trust: owners and auditors must be able to prove that shift readings, fuel dispensing, scale tickets and stock movements were not altered. Editing a signed record in place destroys that proof.

## Options considered

1. Mutable records with audit log — **Pros:** Familiar CRUD. **Cons:** The audit log becomes the only evidence; easy to bypass.
2. Full event sourcing everywhere — **Pros:** Complete history. **Cons:** Heavy for master data; steep learning curve.
3. **Append-only facts + versioned corrections; CRUD only for master data** (chosen) — **Pros:** Strong evidence where it matters, simple elsewhere. **Cons:** Queries must pick the current version.

## Decision

Signed or verified records are never updated or deleted. A correction creates a new version linked to the original (controller correction) or a counter-signed reversal plus a new record (scale tickets, stock adjustments). The original stays visible forever. Database triggers reject UPDATE/DELETE on fact tables; master data (assets, customers) remains mutable with audit.

## Consequences

- ✅ Evidence is preserved by construction; corrections are explicit and attributable.
- ⚠ Read models must expose "current version" views; storage grows (acceptable).
- 🔎 Revisit only if a legal requirement mandates erasure (handled by crypto-shredding of personal data, not by deleting facts).

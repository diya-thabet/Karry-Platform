---
title: "ADR-0008: Hash-chained audit ledger with WORM anchoring"
area: adr
tags: [ledger, audit, hash-chain, worm, tamper-evidence]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0008 — Hash-chained audit ledger with WORM anchoring

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** US-P1-AUD-01..04; [trust-layer-signatures-audit.md](../02-architecture/trust-layer-signatures-audit.md)

## Context

A plain audit table can be edited by anyone with database superuser access. Customers and auditors need tamper-**evidence**: the ability to detect any alteration of history.

## Options considered

1. Audit table only — **Pros:** Simple. **Cons:** Silent tampering possible.
2. Public blockchain — **Pros:** External verifiability. **Cons:** Cost, latency, privacy and regulatory concerns.
3. **Per-tenant hash chain in PostgreSQL + periodic anchoring to object storage with Object Lock** (chosen) — **Pros:** Detects tampering; cheap; private. **Cons:** Needs a verifier tool and anchoring job.

## Decision

Every business state change appends a ledger entry **in the same transaction** (`entry_hash = SHA-256(prev_hash ‖ tenant ‖ seq ‖ type ‖ payload_hash ‖ occurred_at)`, canonical payloads), with a gap-free sequence per tenant. The app role has INSERT/SELECT only; triggers reject UPDATE/DELETE/TRUNCATE. An hourly job anchors chain heads to object storage with Object Lock (compliance mode). A verifier CLI and an admin "integrity check" recompute the chain against anchors.

## Consequences

- ✅ Any edit to history is detectable at the exact sequence number.
- ⚠ Per-tenant serialization of ledger appends (advisory lock), ample for expected write rates (< 1 write/s average).
- 🔎 Revisit if per-tenant write rates exceed ~200/s sustained.

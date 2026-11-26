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

## Implementation notes (P1.6, 2026-10-23)

Decision unchanged. Details in [trust layer §13.3.1](../02-architecture/trust-layer-signatures-audit.md#1331-as-implemented-p16-trust-module-schema-audit):

- The fields are joined with `\n` and hashed as UTF-8 text, with lowercase hex, `D`-form UUIDs and microsecond UTC timestamps. This makes the encoding unambiguous and reproducible by an external auditor; "‖" in the formula left it open.
- Serialization uses a lock on the per-tenant chain-head row rather than an advisory lock, because the head row also stores `last_seq`/`last_hash`.
- Payloads are RFC 8785 canonical in strict mode: exact decimals must travel as strings.
- Anchors are signed canonical documents in a versioned Object Lock bucket. Compliance retention stops deletion and alteration of a version but not the upload of a newer version, so the verifier trusts the **oldest** version of each key and reports any key with several versions.
- The alert `KarryLedgerAnchoringStale` fires after 2 h without a fully successful run, or when the worker reports nothing (runbook in `docs/06-operations/runbooks/`).

## Amendment: deferred append for long transactions (P2.12a, 2026-11-26)

Appending locks the tenant's ledger head until the transaction commits, which keeps the sequence gap-free. A long
transaction that appends early therefore makes every other writer of the tenant wait for its end. The P2.12a shift
change found this with a tablet's batch (finding L-5 in [load testing](../05-quality/load-testing.md#4-findings)):
uploads of one company ran one at a time.

**Decision:** such a transaction may defer its ledger (`LedgerDeferral` in BuildingBlocks). Its owner marks it at
the start; the ledger writer then keeps the entries instead of appending; the owner appends them right before the
commit. The rule of this ADR holds: the entries are written in the same transaction as their change and commit or
roll back with it, in the order they were given. A rollback to a savepoint drops the entries given after it. Only
the sync session defers today; every other unit of work appends as before. A transaction that marks itself and
commits without the final append would lose its entries, so the session appends in its commit path, and tests
check the chain after deferred batches (`LedgerIntegrationTests`, the sync suites).


---
title: "ADR-0044: Scale ticket"
area: adr
tags: [adr, phase-3, p3-4, weighbridge, ticket, signatures, units, immutability]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.4), US-P3-WB-01 AC1–AC5, US-P3-WB-04"
status: proposed
last_updated: 2026-11-27
---

# ADR-0044: Scale ticket

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

A ticket is the money leaving the quarry: two weighings, the net, a frozen price, a signature, a PDF. Mass is the
authoritative measure; volumes are derived from it (spec review M2).

## Decision

### 1. One ticket, two signed passes

`first_pass` and `second_pass` (weight in kg, time, source `MANUAL` in P3, indicator photo hash), direction inbound or
outbound, truck, driver, customer, product, optional order. Net in kg = second − first (outbound) or first − second
(inbound); never negative on a ticket (reversals carry the sign). Tonnes and, for `m3 LOOSE` customers, volume are
derived through the units engine with the **density version id** in force at the second pass, stored on the ticket.

### 2. Rules at issue

Blocked customer → refused unless the site manager's override (RULE-WB-07, audited). Stored-tare single pass only when
the tenant allows it, flagged `STORED_TARE`. A manual weight needs an in-app indicator photo (RULE-WB-05).

### 3. Immutable

The weighmaster's signature envelope covers weights, photo hashes, parties, product and price. The table is
append-only (trigger, revoked `UPDATE`/`DELETE`); corrections only by reversal (ADR-0042's sibling flow, P3.6).

## Consequences

- A ticket can be verified by anyone from its QR (ADR-0040's public page).
- Weighbridge records join Verification (`IReviewableRecord`) and Integrity (rules) without new engines.

## Alternatives considered

| Option | Why not |
|---|---|
| Store tonnes only | Loses the scale's resolution and the audit trail back to the indicator reading |
| Editable until printed | Printing is not a control point; the signature is |

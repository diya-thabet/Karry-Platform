---
title: "ADR-0045: Weighbridge desk"
area: adr
tags: [adr, phase-3, p3-5, weighbridge, ux, field-app, offline, printing]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.5), US-P3-WB-01 (UI), US-P3-WB-03 AC2"
status: proposed
last_updated: 2026-11-27
---

# ADR-0045: Weighbridge desk

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

The weighmaster works at a desk (PC or tablet), often with an unreliable network, issuing a ticket every minute or
two at peak. The field app already has enrollment, an offline outbox, device signatures and the in-app camera.

## Decision

### 1. Desk mode of the field app

An enrolled device with the `weighbridge` role opens the desk: keyboard-first, wide layout from 1024 px (tablet layout
below), plate search, pass 1 and pass 2 screens with the next field focused, an "open trucks" list (first pass done),
warnings before signing (blocked customer, over payload, tare deviation), weighmaster signature, outbox.

### 2. Printing

Browser printing with print CSS for A5 and 80 mm thermal; no printer driver integration in P3. Reprint from the ticket.

### 3. Weight entry

Manual in P3 with a mandatory indicator photo; automatic indicator capture is P6's edge agent (RULE-WB-08 then).

## Consequences

- One offline, signing and enrollment stack for every field role.
- Desk timing is a budget (≤ 90 s per truck, usability session in P3.5).

## Alternatives considered

| Option | Why not |
|---|---|
| A separate desktop application | Duplicates enrollment, outbox and signatures |
| Portal page (online only) | Fails the offline requirement (AC6) |

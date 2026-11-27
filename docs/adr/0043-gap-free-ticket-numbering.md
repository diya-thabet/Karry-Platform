---
title: "ADR-0043: Gap-free ticket numbering, offline"
area: adr
tags: [adr, phase-3, p3-3, weighbridge, numbering, offline, sync, legal]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.3), US-P3-WB-01 AC4, AC6"
status: proposed
last_updated: 2026-11-27
---

# ADR-0043: Gap-free ticket numbering, offline

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

Tickets need a sequential number per site with no gap (tax and audit practice). Desks can be offline; two desks of
one site must never issue the same number. The legal status of a provisional number is not yet confirmed (WP-P0-06).

## Decision

### 1. Blocks reserved online

A per-site counter in `weighbridge.number_block`. An enrolled desk device reserves the next block (default 50 numbers)
with a signed request while online (`SELECT … FOR UPDATE` on the site's counter, one transaction). Numbers are consumed
in order. A block is never released for reuse: unused numbers are closed by a signed **void range** record, so the
sequence stays explainable with no unexplained gap.

### 2. Tenant policy

- `online_only` (**default** until the legal memo): a desk without numbers left in a block cannot issue a ticket; it
  says so and asks to reconnect.
- `provisional_allowed`: an offline desk with no block issues `P-{device}-{seq}`; on sync, provisional tickets get
  final numbers in issue order (device sequence); the provisional number stays on the ticket and is printed on reprints.

### 3. Acceptance

A model-based test (FsCheck state machine: several devices, reservations, issues, going offline and online, syncs in
any order) proves the sequence is gap-free (or every gap is a signed void range) and duplicate-free over 10 000 runs.

## Consequences

- No duplicate numbers by construction; gaps only as signed, explained void ranges.
- With `online_only`, an outage at the scale stops ticketing until the network returns (the memo decides whether
  `provisional_allowed` may become the default).

## Alternatives considered

| Option | Why not |
|---|---|
| One global sequence per site assigned at sync | Offline tickets would carry no number at all; drivers leave with paper anyway |
| Releasing unused numbers back | Two devices could later issue the same released number; void ranges are safer |

---
title: "ADR-0046: Fuel storages and stock"
area: adr
tags: [adr, phase-3, p3-8, fuel, tanks, calibration, reconciliation]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.8), US-P3-FUEL-01, US-P3-FUEL-04"
status: proposed
last_updated: 2026-11-27
---

# ADR-0046: Fuel storages and stock

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

Fuel is the largest controllable cost of a quarry and the easiest to steal. Stock is measured by dipping tanks;
dips are converted to litres through each tank's calibration table. A dipstick reads to 0.1 cm.

## Decision

### 1. Storages and calibration

A storage is a fixed tank or a mobile bowser (product, capacity, site, dispenser meter). Its calibration table (dip cm →
litres) is versioned; it must be strictly increasing; litres between points by linear interpolation; a dip outside the
table is refused. Volumes in litres as `decimal(12,2)`; temperature recorded, not corrected, in P3.

### 2. Stock is derived

Stock is never stored: opening + deliveries − dispenses (− transfers) is compared with dips. Litres read from a dip are
rounded to 0.01 L **at the reading** (as a person writes them down); every figure after is exact on those values, so a
reconciliation's figures add up to the digit.

### 3. Reconciliation and its tolerance

Per storage and period (a day by default): variance = opening + delivered − dispensed − closing; variance % of the
period's dispensed volume (of the opening when nothing was dispensed). **RULE-FUEL-04 fires when |variance| is larger
than both 0.5 % (tenant setting) of that base and the dip uncertainty** (what one 0.1 cm step is worth in litres at the
opening and at the closing level, added). A loss smaller than the dipstick can measure cannot be told from a reading
error; flagging it would raise false alerts on quiet days. Found with the P3.0 golden data: a fixed 0.5 % flagged
dipstick noise on light days.

## Consequences

- Reconciliations are reproducible from the stored readings; the golden month (`tests/vectors/fuel-golden-month.json`)
  is the reference (TC-P3-FUEL-002).
- Small, slow losses below the dip uncertainty show only over longer periods (weekly and monthly views).

## Alternatives considered

| Option | Why not |
|---|---|
| Fixed % tolerance only | False alerts on low-throughput days (P3.0 data) |
| Store a running stock | A wrong reading would propagate; derived stock re-computes cleanly on late data |

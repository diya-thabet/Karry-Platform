---
title: "ADR-0047: Fuel analytics: consumption, baselines, anomalies"
area: adr
tags: [adr, phase-3, p3-11, fuel, analytics, baselines, robust-statistics, anomalies]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.11), US-P3-FUEL-06, US-P3-FUEL-07"
status: proposed
last_updated: 2026-11-27
---

# ADR-0047: Fuel analytics: consumption, baselines, anomalies

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

A fixed threshold against a global baseline produces many false positives (spec review M8). Each machine has its
own normal; usage comes from verified shift entries.

## Decision

### 1. Intervals

For each refuel of an asset after its first: litres of that refuel ÷ usage since the previous refuel (ΔH or ΔKM from
verified shift entries, through `FieldOps.Contracts`). A refuel with no usage is not a rate (RULE-FUEL-06). Intervals
whose meter is in failed mode are `ESTIMATED`.

### 2. Baselines

Median and MAD of the asset's previous intervals (at most 20, per activity when known). With fewer than 8, the **type
baseline** (previous intervals of all assets of the type). **MAD is floored at 1 % of the median**, so a very regular
machine does not divide by zero.

### 3. Anomaly (RULE-FUEL-05)

Deviation (r − median) ÷ median above the threshold (18 %, tenant setting; relaxed for `ESTIMATED`) **and** robust z =
0.6745 × (r − median) ÷ MAD above 3.5. Each anomaly stores its explanation: expected and actual rate, the baseline's
source and size, the refuels and shift entries of the interval.

### 4. Determinism and late data

Pure functions over stored facts; recomputed when a late refuel or shift entry arrives (idempotent job). The reference
is `tools/demo/fuel_oracle.py` and the golden month: every interval within 1 % (TC-P3-FUEL-008).

## Consequences

- Alerts are explainable and reproducible; the P3.0 generator shows zero false anomalies on 39 seeds and catches the
  seeded 25 % overconsumption every time.
- New machines are judged against their type until they have 8 intervals.

## Alternatives considered

| Option | Why not |
|---|---|
| Mean and standard deviation | One theft or one mistyped refuel moves the baseline; median/MAD resist outliers |
| Machine learning model | Not explainable to a site manager; not enough data per machine in a pilot |

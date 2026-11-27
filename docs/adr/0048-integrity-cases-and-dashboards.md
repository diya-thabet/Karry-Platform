---
title: "ADR-0048: Integrity cases and dashboards"
area: adr
tags: [adr, phase-3, p3-12, integrity, dashboards, cases, read-models]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.12), US-P3-INT-01, US-P3-FUEL-08"
status: proposed
last_updated: 2026-11-27
---

# ADR-0048: Integrity cases and dashboards

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

Flags come from every module (field, weighbridge, fuel). An executive needs one screen each morning, and someone
must own each problem until it is closed.

## Decision

### 1. Cases

A case groups one or more flags with an assignee, comments and a resolution (with a reason); every action is signed and
ledgered. A flag closed by new facts (ADR-0036) closes its case's part automatically, with the reason.

### 2. Read models

Dashboard tiles and trends read from read models refreshed by events (open flags by severity and type, continuity
exceptions, fuel anomalies, tank variances, ticket flags, reversals, overdue reviews, clock skew, devices unsynced over
24 h), never from raw tables at view time. Budget: < 2 s for 12 months of one site.

### 3. Leaderboards

Ranking by site, controller or weighmaster is behind `integrity.leaderboard.read` and a tenant setting.

## Consequences

- Fast dashboards and one place to work on problems; read models are rebuilt from events if needed.

## Alternatives considered

| Option | Why not |
|---|---|
| Query raw tables per view | Too slow on 12 months; mixes reading with the modules' internals |

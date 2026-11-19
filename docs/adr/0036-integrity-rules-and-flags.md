---
title: "ADR-0036: Integrity rules and flags"
area: adr
tags: [adr, phase-2, p2-7, integrity, rules, flags, continuity, meter-events]
source: "P2.7 planning (sub-phases.md §P2.7, §Decisions), US-P2-FO-07, US-P2-AST-01, US-P2-AST-02, US-P2-INT-01, ADR-0034"
status: proposed
last_updated: 2026-11-19
---

# ADR-0036: Integrity rules and flags

> [Docs index](../README.md) › [ADRs](README.md) · [P2.7 slice plan](../04-phases/phase-02-field-operations/p2.7-integrity-plan.md)

- **Status:** Proposed (2026-11-19, P2.7 planning)
- **Date:** 2026-11-19

## Context

Shift entries are stored as signed facts and flagged, never refused (ADR-0034 §6). Each version carries the flags
known when it was stored, which is what the tablet shows. A controller needs more: a flag that stays open until it is
explained, that closes by itself when a late entry or a meter event explains the gap, and that keeps its history. The
rules (RULE-FO-01 … 07, [detection rules](../03-domain/catalogues/detection-rules.md)) need thresholds a company can
tune, except the security rules.

## Decision

### 1. A rule is code, settings and a flag

A rule's logic lives in the module that owns its facts (FieldOps: continuity, Δ, clock skew, geofence, photos; Sync:
revoked devices). Its settings (on/off, thresholds) live in the Integrity module, per company, with defaults from the
catalogue; RULE-FO-05, 06 and 07 (security) cannot be switched off. What a rule finds is a **flag** in the Integrity
module.

### 2. Flags open and close, never vanish

`integrity.flags`: `{id, rule, severity, subject {type, id}, related [{type, id}], siteId, evidence [links], openedAt,
closedAt?, closeReason?}`, tenant-owned with RLS. A flag is opened once per subject and rule; it is closed with a reason
(`re-evaluated`, later the controller's resolution in P2.8) and never deleted or reopened (a new finding is a new
flag). Opening and closing are ledgered (`integrity.flag_opened.v1`, `integrity.flag_closed.v1`) in the same
transaction as the fact that caused them.

### 3. Modules raise and close flags through a contract

`Integrity.Contracts` exposes `IIntegrityFlags.SyncAsync(subject, rule, findings)`: the module gives the findings that
hold now for a subject (a meter for continuity, an entry for the others); Integrity opens the missing flags and closes
the open ones no longer found. Called in the caller's unit of work, so a fact and its flags commit together. Settings
are read through the same contract; a switched-off rule raises nothing (its open flags stay open until resolved).

As built (slice 1): the Integrity module has its own schema; `IIntegrityFlags.SyncAsync(connection, transaction, …)`
joins the caller's transaction (as the envelope recorder does), so the flags and their ledger entries
(`integrity.flag_opened.v1`, `integrity.flag_closed.v1`) commit with the fact. One open flag per subject, rule and key
(a partial unique index); a trigger lets a row change only from open to closed and refuses deletes. Settings are the
catalogue's defaults until slice 6. `GET /api/v1/integrity/flags` (`integrity.flags.read`, site scope) pages newest
first by the flags' UUIDv7.

### 4. Continuity is evaluated in shift-time order

A pure function takes a meter's current segment versions and meter events ordered by start time and returns every
unexplained discontinuity (the end of one segment against the start of the next, an event's offset in between). It
runs for the meter on every start, end, late arrival and meter event, so a late entry moves the comparison to its real
neighbours (TC-P2-FO-008). It is property-tested (CsCheck): random readings and events give a flag exactly where an
unexplained discontinuity is.

### 5. Meter events are manager decisions

`assets.meter_events`: REPLACED (old final, new initial), RESET (value before, value after), FAILED and REPAIRED, each
with time, reason and photos (evidence blobs), recorded in the portal by a person with `assets.meters.events`
(authenticated, ledgered under their name). REPLACED and RESET carry the offset that keeps accumulated usage continuous.
Tablets pull them; a FAILED meter switches the tablet to operating times and the entries to `estimated` until REPAIRED.

## Options considered

1. **Flags in an Integrity module, rules in the owning modules (chosen).** One place to list, settle and audit flags;
   no module reads another's tables.
2. **Flags only on the entry versions (as in P2.5).** Cannot close by themselves, cannot link two entries, cannot
   carry a resolution. Rejected.
3. **A rules engine with rules as data.** Too early: seven rules, each with its own facts; revisit when companies ask
   for their own rules.

## Consequences

- P2.8's controller console works on Integrity flags; P2.10's notifications subscribe to `integrity.flag_opened.v1`.
- Every module that owns facts gains an Integrity call in its write path; the contract keeps it one call per subject.
- A meter's continuity is recomputed on each of its changes: cheap for the tens of segments a meter has per week.

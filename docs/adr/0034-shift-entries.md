---
title: "ADR-0034: Shift entries: resolved against the site's shift rules, versions and segments, signed records, flags instead of rejections"
area: adr
tags: [field-operations, shifts, shift-entries, meters, segments, handover, flags, signatures, phase-2]
source: "Phase 2 sub-phase P2.0 (decision for P2.4), written in P2.4"
status: proposed
last_updated: 2026-11-09
---

# ADR-0034 — Shift entries

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (2026-11-09, P2.4)
- **Date:** 2026-11-09
- **Deciders:** Product owner, software architect
- **Related:** US-P2-FO-01 (AC1, AC4, AC5), US-P2-FO-04 (AC2), US-P2-FO-05 (AC1–AC2); TC-P2-FO-001, 003, 005, 006,
  011; RULE-FO-01, RULE-FO-02 ([detection rules](../03-domain/catalogues/detection-rules.md)); threats T-01, T-10;
  [ADR-0024](0024-sites-geometry-and-shift-time.md) (shift time), [ADR-0031](0031-sync-command-processing.md)
  (handlers, flags), [ADR-0032](0032-evidence-blobs.md) (photos), [ADR-0033](0033-signature-envelopes-and-server-keys.md)
  (envelopes), [ADR-0025](0025-units-engine-and-materials.md) (explicit units)

## Context

A shift entry is the field's main fact: an operator says "I ran machine EX-01 on the night shift, the hour meter read
12 482.5 at the start and 12 493.0 at the end, the tank was ¾ full". It arrives signed, possibly days late, from a
tablet whose clock may be wrong. Phase 1 built the pieces it stands on: sites with effective-dated shift patterns and
DST rules (ADR-0024), machines with meters (P1.13), signed commands handled in the upload request (ADR-0031), signed
envelopes (ADR-0033). What is still open:

1. **which shift** an entry belongs to, when it arrives late or starts a little early;
2. how an entry **changes** (start, end, a later correction) without ever overwriting what was signed;
3. how a **handover** between two operators is recorded, and who signs what;
4. what a command that carries a **signed record** looks like;
5. which checks the server runs, given that it never refuses a signed fact for a business reason (ADR-0031 §3).

## Decision

### 1. A new module `FieldOps`, schema `fieldops`

Shift entries, and later checklists and stoppage reason codes, live in a new module. It reads sites and shifts from
`Organization` and machines and meters from `Assets` through their contracts, never their tables.

### 2. The shift is resolved from the site's rules at the entry's start time

- `Organization.Contracts` gains `IShiftCalendar.ResolveAsync(siteId, instant)`: the shift instance whose
  `[startsAt, endsAt)` holds the instant, **computed from the rules** (pattern version in effect, non-working days,
  DST as in ADR-0024), not read from the stored 30-day window: an entry that arrives a week late, or for a day the
  generator has not reached, still resolves. It looks at the instant's local date and the day before, so a night shift
  that started yesterday is found (TC-P2-FO-001: 23:30 and 00:30 resolve to the same night shift).
- The instant used is the **start's device time** (when the operator says it happened). Clock skew is measured per
  upload (ADR-0031 §6) and flagged by RULE-FO-03 in P2.7; the shift is not moved by it.
- **Early start:** a start up to **60 minutes before** a shift's start belongs to that shift (operators warm up
  machines). Otherwise, an instant in no shift (a gap between shifts, a non-working day) is stored with no shift and
  the flag `SHIFT_OUTSIDE_PATTERN`. The tablet preselects the shift (US-P2-FO-01 AC1) with the same rule.
- **The plan's `fieldops.shift_instance` table is not built:** `organization.shift_instances` already stores the next
  30 days (ADR-0024), and a second copy would drift. An entry stores the resolved shift as `(shift_date, shift_code,
  shift_starts_at, shift_ends_at)`, the facts as they were at entry time; a later pattern change never rewrites them.

### 3. An entry is a chain of versions, and a shift on a machine is a chain of segments

- `fieldops.shift_entries` is **append-only** (trigger and revoked privileges, as the ledger): one row per **version**,
  key `(root_id, version)`. The start command writes version 1 (`open`), the end command version 2 (`closed`), a
  correction (P2.6) the next one. The current state of an entry is its highest version; every version keeps the
  command, the envelope and the device that produced it.
- An entry is one operator on one machine for one stretch of time: a **segment**. `segment_no` counts the segments of
  a machine in a shift (1, 2, …).
- **Meter values are explicit and exact:** decimal strings on the wire (`"12482.5"`, at most 1 decimal as meters are
  kept in Assets, no sign, no leading zero, never a JSON number), `numeric(9,1)` in the table (largest value
  9 999 999.9), always next to the meter's kind (`hours` or `km`) and the meter's ID (ADR-0025: no bare
  number). Fuel level is a whole percentage 0–100.
- `estimated` marks an entry made while the meter is known to be failed (P2.7 adds failed-meter mode); the field is
  there from the first version so the shape does not change.

### 4. Handover: the outgoing operator ends, the incoming operator starts; each signs their own fact

US-P2-FO-05 AC1 is built **without** a separate `handover` command type (the plan listed `.handover.v1`). The outgoing
operator ends their segment (`end.v1`), and the incoming operator starts the next one (`start.v1` with
`previousRootId`). Each is signed by the person who states it (T-01, T-07): a single handover command would carry one
signer for two people's facts. The server sets `segment_no = previous + 1` and checks that the new start equals the
previous end (TC-P2-FO-006); a difference is flagged `RULE-FO-01`.

### 5. A command carrying a signed record

```json
{
  "record": { "rootId": "uuidv7", "version": 1, "siteId": "…", "assetId": "…", "meterId": "…", "…": "…" },
  "envelope": { "v": 1, "recordType": "fieldops.shift_entry", "recordId": "<rootId>", "recordVersion": 1,
                "payloadHash": "sha256:<RFC 8785 of record>", "…": "…" },
  "deviceSignature": "<ES256 over the envelope's canonical text>"
}
```

The handler checks the record's shape, that `envelope.payloadHash` is the hash of the record's RFC 8785 form, and that
`recordId`/`recordVersion`/`recordType` name this record; then it stores the envelope through `IEnvelopeRecorder`
(ADR-0033) and the record **in the same transaction**. The operator is `envelope.signer.userId`, the person unlocked
by PIN on the tablet (T-01), never a field of the record. A structural problem (bad shape, hash mismatch, refused
envelope) is a `Rejected` outcome (ADR-0031 §3); the record is never stored without its envelope.

### 6. Checks: flags, never rejections

The handler stores every signed fact and returns `Conflict(flags)` when it needs a look (ADR-0031 §3). Flags in P2.4:

| Flag | When |
|---|---|
| `RULE-FO-01` | The start differs from the end it continues: in a handover, the previous segment's end (not compared while that segment is still open); otherwise the meter's last end at or before the start (by the end's time, among the entries already received). No meter event explains it yet (meter events come in P2.7) |
| `RULE-FO-02` | Δ < 0, or for an hour meter Δ > the segment's real length + 0.5 h ([detection rules](../03-domain/catalogues/detection-rules.md)); an oversized Δkm waits for the tenant thresholds of P2.7 |
| `SHIFT_OUTSIDE_PATTERN` | The start is in no shift of the site (§2) |
| `ASSET_NOT_ON_SITE` | The machine is not (or no longer) assigned to the command's site, or the meter is not the machine's |
| `END_BEFORE_START` | The end's time is not after the start's |
| `OVERLAP` | The same operator has another segment sharing time with this one, `[start, end)`, a segment without an end still running; checked at the start and again at the end (TC-P2-FO-011; US-P2-FO-05 AC2 is enforced on the tablet, flagged on the server) |

Flags are stored on the version that raised them. P2.7 moves them into the `Integrity` module with explanations and
severities, re-evaluates continuity in shift-time order and closes flags that no longer hold.

### 7. Reading entries

The portal reads entries by site, shift, machine and operator, and an entry with all its versions
(`GET /api/v1/fieldops/entries…`), under the site scope of ADR-0022, with masking where a policy hides a field (T-10).

### 8. As built (P2.4)

- **Slice 1, shift resolution:** `ShiftCalendar.Resolve` (Organization.Domain, pure, NodaTime) and `IShiftCalendar` /
  `ResolvedShift` (Organization.Contracts) implemented by `ShiftResolution` over the site's pattern versions and
  non-working days; it computes the local date before and after the instant, prefers the shift running at the instant,
  then one starting within `ShiftCalendar.EarlyStart` (60 min). Tests: examples (23:30 and 00:30 → the same night
  shift, early start, gap, holiday, an early start before a midnight shift) and a property over random zones and
  patterns (every instant inside a computed shift resolves to it) in `ShiftCalendarTests`; an entry 45 days old,
  outside the stored window, resolved through the real store in `ShiftsApiTests` (TC-P2-FO-001 server part).
- **Slice 2, entries and their handlers:** module `FieldOps` (schema `fieldops`, created with `tools/new-module.sh`).
  `fieldops.shift_entries`: key `(tenant_id, root_id, version)`, RLS, append-only (trigger `fieldops.reject_change()`
  and `UPDATE`/`DELETE`/`TRUNCATE` revoked from `karry_app`), unique `command_id`, `numeric(9,1)` meter values,
  `meter_kind` checked to `hours`/`km`, stoppages as `jsonb`, flags as `text[]`. `Assets.Contracts` gained
  `IMeterDirectory` (meter, machine, kind, status and the machine's site assignments; `MeterDirectory` in
  Assets.Infrastructure). Handlers `fieldops.shift_entry.start.v1` and `.end.v1` (`StartShiftEntry`,
  `EndShiftEntry`): strict record reading (exact members, no nulls, UUID/instant/meter-value/hash formats),
  `SignedRecord.Read` binds the envelope to the record (type, ID, version, `payloadHash`), the envelope is stored
  through `IEnvelopeRecorder` in the same transaction. Rejections: `fieldops.entry_invalid`,
  `fieldops.envelope_mismatch`, `fieldops.meter_unknown`, `fieldops.entry_exists`, `fieldops.version_conflict`
  (final); `fieldops.entry_unknown` (an end before its start) and `fieldops.previous_unknown` (a handover before
  the previous segment) are retryable; the envelope recorder's codes pass through (final). Flags built:
  `RULE-FO-02`, `END_BEFORE_START`, `SHIFT_OUTSIDE_PATTERN`, `ASSET_NOT_ON_SITE`; `estimated` when the meter is
  `failed`; a handover start takes `segment_no = previous + 1`. Stoppage reasons are a fixed list for now
  (`breakdown`, `weather`, `no_material`, `no_operator`, `other`). Tests: `ShiftEntryHandlersTests` (PostgreSQL, real
  envelope recorder; TC-P2-FO-005, TC-P2-FO-010, TC-P2-SIG-002 binding, append-only and RLS) and the directory in
  `MetersApiTests`. Continuity `RULE-FO-01` and `OVERLAP` come in slice 3.
- **Slice 3, continuity and overlaps:** `ShiftEntryRules.StartFlags` (RULE-FO-01) and `ShiftEntryRules.Overlaps`
  (pure); the store reads the meter's last end at or before a start and the operator's other segments (current
  versions) that may share time. The start flags `RULE-FO-01` and `OVERLAP`, the end `OVERLAP` again over its
  now-known length. Tests: handler cases for TC-P2-FO-003, 006, 007 and 011 in `ShiftEntryHandlersTests`; properties
  over 10 000 random cases each in `ShiftEntryRulesTests` (Δ flagged exactly when negative or beyond the segment +
  0.5 h on an hour meter, only when negative on a km meter; end-before-start; continuity; overlaps match a
  minute-by-minute check; every stored meter value reads back from its wire text).
- **Slice 4, the portal's read API:** permission `fieldops.entries.read` (site scope; Tenant Admin, Executive, Finance,
  Controller, Site Manager, Maintenance Planner). `GET /api/v1/fieldops/entries` lists current versions newest first
  (entry ID order, UUIDv7), filters `siteId`, `shiftDate`, `shiftCode`, `assetId`, `operatorId`, `status`
  (`open`/`closed`), `flagged`, pages by `cursor` (the last entry ID) and `limit` (1–200, default 50);
  `GET /api/v1/fieldops/entries/{entryId}` gives `entry` (the current version) and `versions` (oldest first), 404
  `fieldops.entry_not_found` outside the caller's sites. Each version carries its meter kind and unit (`h`, `km`) and
  `usage` (end − start once closed). **No field is masked:** a shift entry holds no price, cost, margin or wage (T-10);
  machine costs per hour come with Finance (Phase 7) and will be masked there. Tests: `EntriesApiTests` (HTTP,
  PostgreSQL, site scope), the tenant-isolation suite (seeded entry, attack value `entryId`).
- **Stoppage reasons stay a fixed list** (`breakdown`, `weather`, `no_material`, `no_operator`, `other`) until the
  owner confirms the real codes (sub-phases §7 open questions); the tenant-editable catalogue the plan lists is built
  then, as data, without changing the command shape (a reason is a code).

## Options considered

1. **Resolve against the rules, append-only versions, each operator signs their own segment (chosen).**
2. **A `fieldops.shift_instance` table materialised lazily (as the P2.0 plan said).** It duplicates
   `organization.shift_instances` and drifts after a pattern change. Rejected (§2).
3. **Resolve by receipt time.** Wrong for every entry made offline. Rejected.
4. **A single handover command.** One signature for two people's facts. Rejected (§4).
5. **Update the entry row in place at the end of the shift.** Simpler queries, but the signed start would be
   overwritten. Rejected: versions.

## Consequences

- **Positive:** late entries resolve correctly; nothing signed is ever overwritten; each person signs only what they
  state; the server never loses a fact over a business rule.
- **Negative:** reading an entry means picking its highest version; continuity and overlaps are checked against the
  entries received so far (a late entry does not re-flag the ones before it) until P2.7 re-runs them in shift-time
  order; a start more than 60 minutes early is flagged even when it was legitimate.
- **Follow-ups:** P2.5 builds the tablet flows on the same rules (shared validation vectors); P2.6 adds corrections as
  versions; P2.7 adds meter events, Integrity flags and the Δkm threshold.

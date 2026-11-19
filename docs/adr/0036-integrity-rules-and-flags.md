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

As built (slice 2): `Continuity.Find` (FieldOps domain) orders a meter's current segment versions by start time, then
entry ID, and compares each start with the end just before it; a segment still running has no end, so the next start
is not compared (a missing end is RULE-FO-08). The start and end handlers save the entry, re-evaluate the whole meter
and call `SyncAsync` (subject `meter`, key `<before>><after>`, both entries related, the end and start photos as
evidence, the values in `details`); a finding may name its own site (`Finding.SiteId`, the later entry's site, for a
meter that moved). The end also syncs RULE-FO-02 on the entry (key `delta`). A module context may join the same upload
transaction twice (`ModuleDbContext.JoinAsync` is idempotent for that transaction). The property test is CsCheck;
the mutation check was run by hand (eight mutants of `Find` and `Difference`, all killed): Stryker's first test run
fails on the Windows machine with the database tests, so a nightly Stryker step for FieldOps waits for a run on CI.
Residual: the whole meter is read on every start and end (indexed on tenant, meter, start; fine for years of
shifts, to revisit with a window if a meter's history grows large).

### 5. Meter events are manager decisions

`assets.meter_events`: REPLACED (old final, new initial), RESET (value before, value after), FAILED and REPAIRED, each
with time, reason and photos (evidence blobs), recorded in the portal by a person with `assets.meters.events`
(authenticated, ledgered under their name). REPLACED and RESET carry the offset that keeps accumulated usage continuous.
Tablets pull them; a FAILED meter switches the tablet to operating times and the entries to `estimated` until REPAIRED.

As built (slice 3a): `assets.meter_events` (append-only, RLS, ledgered `assets.meter_event_recorded.v1`), recorded
through `POST /api/v1/assets/{assetId}/meters/{meterId}/events` by a person who manages the machine
(`assets.registry.manage`: the same people as a separate `assets.meters.events` would name, so no new permission). The
meter keeps its ID through a replacement (entries and tablets keep naming it); REPLACED and RESET add before − after to
its offset and make the value after its last reading; FAILED stops it, REPAIRED makes it count again from the repair
reading. Photos are `sha256:` references (1 to 5); checking that they exist comes with the portal form (slice 3b).
FieldOps reads the events through `IMeterEvents` on every start and end, and is told of a new one through the Assets
outbox (`IMeterEventSubscriber`, in the dispatcher's transaction), so the gap an event explains closes. In the check,
the events between two starts (after the first, up to the second) are read in time order: a replacement or reset must
start from the value due and moves it to its value after, a failure leaves nothing due, a repair makes its reading due;
an estimated segment is not compared. Hand mutation check: 11 mutants, all killed.

As built (slice 3b): photos are uploaded before the event (`POST /api/v1/assets/{assetId}/meters/{meterId}/evidence`, a
JPEG, PNG or WebP of at most 5 MB, kept under the company's prefix by its SHA-256) and read back by fingerprint
(`GET …/evidence/{sha256}`); an event naming a photo that is not there is refused (`assets.meter_event_photo_missing`).
The portal records events on the meter of a machine, in place of the old "mark failed / working" buttons; the status
endpoint stays for the API but the screens change a meter only through events.

As built (slice 4): the tablets already pull each meter's status (`meters`). On a failed meter the tablet records the
entry on it (a machine with no working meter is no longer "without meter"), asks no start reading (the start is the
last known end, else 0.0) and, at the end, asks the operating time (hours) or distance (km); the end is the start plus
it. The signed record keeps its shape (the shared vectors are unchanged); the server marks the entry `estimated`
because the meter was failed at the start, Δ is checked as for any entry, and continuity does not compare estimated
segments. A REPAIRED event makes the meter count again, and the next start asks a reading.

As built (slice 5a): the photo rules judge each signed start and end on its own (subject `signature_envelope`, its
envelope ID, linked to the entry), so an end never closes its start's flag: RULE-FO-05 when the command carries no
capture record for its photo, RULE-FO-06 when the photo is on another entry (indexed by fingerprint) or is both the
start and end photo of one entry; the later use is flagged. RULE-FO-07 is raised by Sync on each command a revoked
tablet uploads (subject `sync_command`, linked to the `device`), in the transaction that quarantines it. Correction to
slice 3a: the Assets outbox is dispatched in `Karry.Workers`, which did not load FieldOps and Integrity, so a meter
event never re-evaluated continuity outside the tests; Workers now loads both (checked by running `anchor-now`).
RULE-FO-03 needs a measured skew: the tablet corrects its requests with its known offset, so the server cannot
tell a wrong clock from an offline delay; slice 5b has the tablet report its offset with each signed fact.

As built (slice 5b): the tablet sends, with each signed start and end, the clock offset it last measured
(`clock: {"offsetMs": server − tablet}`, a payload member beside `captures`, covered by the command's device
signature). It is not put in the envelope's context: the envelope is a versioned format with shared vectors, and a
new member would be a v2 for one number. A `clock` that is not exactly a whole `offsetMs` within a year is refused
(`fieldops.entry_invalid`); an older app sends none (offset 0). RULE-FO-03 (`ClockSkew.Judge`, pure, CsCheck
properties) judges each signed fact (subject its envelope, linked to the entry): `offset` when the known offset is
beyond `maxSkewMinutes` (10), `ahead` when the envelope's device time corrected by it is later than the server
received the fact by more than that (an offline delay only makes a fact older). A clock moved back while offline and
put right before the next contact cannot be told from an offline delay; this stays a known limit (threat T-04).
RULE-FO-04: Organization's `ISiteGeofence` gives the distance in metres from the envelope's `context.geo` to the
site's drawn boundary (PostGIS `ST_Distance` on the geography, 0 inside); beyond `marginMetres` (200) the signed fact
is flagged, with the distance, the position's accuracy and the margin. Without a position (location refused) or a
boundary nothing is judged. Both rules read their thresholds from `IIntegrityFlags.SettingsAsync` (slice 6 makes them
company settings).

As built (slice 6): `integrity.rule_settings` holds a company's settings of a rule (one row per rule, RLS, ledgered
`integrity.rule_settings_changed.v1` under the person's name, never deleted: going back to a default is a change). A
rule without a row runs with the catalogue's defaults. Each threshold has a range, a number of decimals and a unit in
the catalogue: RULE-FO-02 `toleranceHours` 0 to 4 h (tenths), RULE-FO-03 `maxSkewMinutes` 1 to 1440 min, RULE-FO-04
`marginMetres` 0 to 5000 m. RULE-FO-05, 06 and 07 cannot be switched off: the API refuses it
(`integrity.rule_locked`), a check constraint refuses the row, and `SettingsAsync` runs a security rule whatever is
stored. `GET /api/v1/integrity/rules` is open to everyone who reads flags; `PUT /api/v1/integrity/rules/{rule}` needs
`integrity.rules.manage` (Tenant Admin, privileged) and a recent sign-in, since it decides what gets flagged; `If-Match`
carries the rule's version (`"0"` while the company runs the defaults). A switched-off rule raises nothing new and its
open flags stay open until resolved (§3). `SettingsAsync` reads the settings in the caller's transaction once per unit
of work (`SyncAsync` joins the transaction before reading them). RULE-FO-02's Integrity flag now uses the company's
tolerance; the entry's own flag (what the tablet shows offline, shared vectors) keeps the catalogue's 0.5 h, so the two
may differ for a company that changed it. The portal page (Settings › Integrity rules) explains each rule in the five
languages; the flag list of slice 7 reuses those texts.

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

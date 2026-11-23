---
title: "ADR-0037: Verification engine"
area: adr
tags: [adr, phase-2, p2-8, verification, review, separation-of-duties, four-eyes, sla, policies]
source: "P2.8 planning (sub-phases.md §P2.8, §Decisions), US-P2-VER-02 … 06, US-P2-FO-08, ADR-0033, ADR-0034, ADR-0035, ADR-0036"
status: proposed
last_updated: 2026-11-23
---

# ADR-0037: Verification engine

> [Docs index](../README.md) › [ADRs](README.md) · [P2.8 slice plan](../04-phases/phase-02-field-operations/p2.8-verification-plan.md)

- **Status:** Proposed (2026-11-23, P2.8 planning)
- **Date:** 2026-11-23

## Context

Every shift entry is stored as the operator signed it and flagged, never refused (ADR-0034 §6); integrity flags
open and close over time (ADR-0036). A controller now has to decide on each entry: approve it, reject it, or correct
it with a before-and-after trail; big corrections need a second person; nobody approves their own work
(US-P2-VER-02 … 06). A continuity gap needs a reasoned resolution (US-P2-FO-08). The sub-phase card reserved
"ADR-0035" for this decision; that number went to the pre-start checklists in P2.6, so the verification engine is
ADR-0037 and the planned notifications and documents ADRs become ADR-0038 and ADR-0039.

## Decision

### 1. A Verification module reviews any record type through a contract

A new module `Verification` (own schema, RLS, outbox, ledger) holds the review state; it knows no shift-entry field.
A record type plugs in through `Verification.Contracts.IReviewableRecords`, implemented by the owning module (FieldOps
for `fieldops.shift_entry`, later weighbridge tickets, fuel, counts):

- **facts** of one record version: company, site, record type, version, the hash of the version as signed, the
  people who produced it (the operator), the asset and the time window it covers, its open flags (integrity and the
  entry's own), and the values a policy can test (Δ, meter kind);
- **correct**: apply corrected fields as a new version of the record in the caller's transaction, returning its hash
  and the before → after of every changed field;
- **corrections in bounds**: whether a correction stays within the policy's four-eyes thresholds.

The owning module raises "a version is ready for review" through its outbox (FieldOps: an entry's end, and each
corrected version); a Verification subscriber creates the review item in the dispatcher's transaction.

### 2. One review item per record version, pinned to a policy version

`verification.review_items` has one row per reviewable record version: `pending` → `approved`, `rejected`,
`corrected` or `awaiting_second`; `escalated` is a mark on a pending item, not a state. The item keeps the policy
version in force when it was created (TC-P2-VER-007), its due time, and an `xmin` version for `If-Match` (two
controllers on one item: one wins, the other gets `409 request.version_conflict`, TC-P2-VER-008).
`verification.review_actions` is append-only: approve, bulk approve, reject (reason code + comment), correct (reason,
before → after), second approve, second refuse, auto approve, escalate. Every action is ledgered in its transaction.

### 3. "Signed" means authenticated, ledgered and hash-bound

Portal users hold no device key (ADR-0035 §5 settled this for safety-hold releases). An action is the authenticated person's
decision, ledgered under their name **with the hashes of every record version it covers** (a bulk approval lists
them all in one ledger entry, US-P2-VER-02 AC2), so the hash-chained ledger binds the decision to exactly the
versions the person saw. Correcting and second-approving need a recent sign-in (they change or override a signed
fact); approving and rejecting do not (a controller approves dozens an hour, P2.9's target). A server-signed decision
document, if the owner wants one in the PDF, comes with the shift report (P2.11, ADR-0039).

### 4. Separation of duties is evaluated on the server, at action time

An action is refused (`403 verification.sod_*`) when the reviewer is the record's producer, operated the same asset
in an overlapping window, or (second approval) is the person who corrected it. "The reviewer" is a person, not an
account: a controller's portal account and their own field account are one person when the company administrator
has **linked** them (Identity: an office user's optional link to a field user, ledgered). Without a link the two
accounts are treated as two people; the link is part of the SoD review in the security review. Permissions are read
at action time, so a role removed after the queue loaded refuses the action (TC-P2-VER-009). Bulk approval refuses
the whole batch if any item is flagged or fails SoD (TC-P2-VER-002), and says which.

### 5. Policies are versioned; auto-approval is a recorded action

`verification.review_policies`: per record type, versions with an `effective_from`, holding who reviews (permission
`verification.items.review`, site scope), auto-approval (for shift entries: no open flag and Δ within the norms),
four-eyes thresholds (shift entries: a meter change beyond 2 h or 5 %), SLA hours and the escalation role. Changing a
policy needs `verification.policies.manage` (Tenant Admin, privileged, recent sign-in) and is ledgered; the person
who changes it is recorded on every auto-approval made under that version, so self-approval through a policy edit is
visible (threat T-13). Auto-approval is an action by `policy:<version>`, ledgered, and leaves the item as reviewed as
any other (TC-P2-VER-006).

### 6. SLA and escalation run as a Quartz job

Each item's due time is its creation plus the policy's SLA hours, counted in the site's time zone. A Workers job
(Quartz, like the other jobs) marks overdue items `escalated` once, ledgered, and raises an event for P2.10's
notifications; the queue shows them first (TC-P2-VER-005). The daily digest (US-P2-VER-06 AC2) is P2.10's.

### 7. Continuity gaps are settled with a reason

A controller settles an open RULE-FO-01 flag (US-P2-FO-08) with a reason code: data-entry error (with a correction of
the entry, §1), meter replaced, meter reset or rollover, maintenance run between shifts, towing or transport,
unexplained. Integrity gains "settle a flag" (closed with the reason and the person, ledgered; the flag is never
reopened). Every reason but data-entry error needs a second approval by another person. "Unexplained", or a gap left
open beyond 48 h, opens `RULE-FO-11 FRAUD_SUSPECTED` (critical, security rule) on the meter.

### 8. The tablet learns the outcome at its next pull

A rejected entry shows as rejected with the reason in the operator's history at the next pull (the `entries` sync
source carries the review outcome); a resubmission is a new entry linked to the rejected one (`previousRootId` kept
as it is today for handovers, plus `resubmits`). The push and the in-app notice are P2.10's.

## As built

As built (slice 1): module `Verification` (schema `verification`, RLS, ledgered). A record version is submitted
through `Verification.Contracts.IReviewIntake.SubmitAsync(connection, transaction, submission)` **in the owning
module's transaction**, as integrity flags are (ADR-0036 §3), not through an outbox as §1 first said: FieldOps has no
outbox, and the item must commit with the version it reviews. The submission is a snapshot of the version's facts
(record type, ID, version, hash as signed, site, producer, asset, covered window, flags), kept on the item for the
decisions of slice 2; one item per version (unique index), a repeated submission changes nothing, a review item is
never deleted (`DELETE`/`TRUNCATE` revoked from `karry_app`). FieldOps submits each ended version with the hash the
operator signed (the end envelope's `payloadHash`). `GET /api/v1/verification/items` (`verification.items.read`,
site scope: Tenant Admin, Executive, Controller, Site Manager) lists the caller's sites' items oldest first:
`status` pending (pending and awaiting a second approval, the default), reviewed or all. `review_actions` comes with
the first decision (slice 2).

As built (slice 2): `POST /api/v1/verification/items/{itemId}/approve` and `…/reject` (reason code of
`RejectionReasons` and a comment of 10 to 500 characters) with `verification.items.review` (site scope: Tenant Admin,
Controller, Site Manager) and `If-Match` on the item's version: a stale version or a lost race is `409
request.version_conflict`, a decided item `409 verification.item_decided`. An item outside the caller's sites,
judged when the decision is made, is `404`. Separation of duties: the reviewer's office account gives their linked
field account through Identity's `IReviewers`; equal to the producer is `403 verification.sod_own_record`; a segment
of that person on the item's machine during the time it covers, answered by FieldOps' `IReviewableRecords` (segments
run as for OVERLAP), is `403 verification.sod_operated_asset`. A decision writes an append-only `review_actions` row
(`UPDATE`/`DELETE`/`TRUNCATE` revoked) and a ledger event carrying the record's hash
(`verification.review_item_approved.v1`, `…_rejected.v1`). The link is `PUT /api/v1/identity/users/{id}/field-account`
(`identity.users.manage`, recent sign-in, `identity.user_field_account_linked.v1`; one office account per field
account, a unique index). Its portal screen and the tablet's view of a rejection are slice 2b.

As built (slice 2b): the users API returns an office account's `linkedFieldUserId`; Settings › Users shows an office
user's field account and lets a user administrator choose one of the company's active field accounts, or none,
confirming their identity first (the API checks the recent sign-in once the user is the caller's to manage). Someone
who reads users only sees the link. The tablet's view of a rejection moved to slice 2c.

As built (slice 2c): Verification is a sync source: `reviewOutcomes` holds the decided shift entries of the tablet's
site whose time began in the last 14 days (the operator history's window), with the rejection's reason code and
comment, whole at every pull (`replace`: a few hundred rows at most). The tablet keeps them in offline schema v7
(`reviewOutcomes`, by entry); "My history" shows a stored entry as approved, or as rejected by the controller with the
reason and the comment, the newest decided version counting; the tablet's own states (still to send, refused by the
server) stay over a decision. A resubmission links to the rejected entry when resubmitting is built (§8 keeps it).

As built (slice 3): `POST /api/v1/verification/items/approve` takes 1 to 100 different items, each with the `version`
the reviewer saw. All or none: an item outside the caller's sites (`404`), at another version (`409`), already decided
(`409`), with a flag (`400 verification.bulk_flagged`, listing them) or refused by separation of duties (`403`) stops
the batch, and nothing is approved. The decision is a `ReviewBatch` (append-only `review_batches`, RLS) whose one
ledger event, `verification.review_items_bulk_approved.v1`, lists every item and the hash of its record version
(US-P2-VER-02 AC2); each item gets an approve action carrying the batch ID and raises no event of its own.

## Options considered

1. **A generic Verification module with a record contract (chosen).** One review engine for every record type of
   Phases 2–5; SoD and four-eyes written once.
2. **Review state as columns of each record type.** Fast for shift entries, then copied for tickets, fuel and counts,
   each with its own SoD bugs.
3. **A server-signed decision envelope per action now.** Adds a signing call on every approval (OpenBao round trip,
   ADR-0033) for a guarantee the hash-chained ledger already gives; kept for the printed report (P2.11).
4. **SoD by account only.** Simple, but a controller who also drives would approve their own shifts under their
   other account: rejected; the link of §4 is cheap.

## Consequences

- **Positive:** one audited path for every decision on every record type; policies explain every auto-approval;
  corrections never overwrite a signed fact.
- **Negative:** a company must link a controller's two accounts for SoD to see them as one person; until then the
  security review lists it as a residual. Corrected versions carry a server-computed hash, not a device signature.
- **Follow-ups:** P2.9 builds the controller console on this API; P2.10 delivers escalation and rejection notices;
  P2.11 prints decisions on the shift report.

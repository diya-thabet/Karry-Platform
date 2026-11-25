---
title: "ADR-0037: Verification engine"
area: adr
tags: [adr, phase-2, p2-8, verification, review, separation-of-duties, four-eyes, sla, policies]
source: "P2.8 planning (sub-phases.md §P2.8, §Decisions), US-P2-VER-02 … 06, US-P2-FO-08, ADR-0033, ADR-0034, ADR-0035, ADR-0036"
status: accepted
last_updated: 2026-11-24
---

# ADR-0037: Verification engine

> [Docs index](../README.md) › [ADRs](README.md) · [P2.8 slice plan](../04-phases/phase-02-field-operations/p2.8-verification-plan.md)

- **Status:** Accepted (2026-11-24, P2.8 close-out; security review in §9)
- **Date:** 2026-11-23

## Context

Every shift entry is stored as the operator signed it and flagged, never refused (ADR-0034 §6); integrity flags
open and close over time (ADR-0036). A controller now has to decide on each entry: approve it, reject it, or correct
it with a before-and-after trail; big corrections need a second person; nobody approves their own work
(US-P2-VER-02 … 06). A continuity gap needs a reasoned resolution (US-P2-FO-08). The sub-phase card reserved
"ADR-0035" for this decision; that number went to the pre-start checklists in P2.6, so the verification engine is
ADR-0037 and the planned notifications and documents ADRs become ADR-0038 and ADR-0039 (later ADR-0039 and ADR-0040:
ADR-0038 went to the controller console, P2.9).

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
document, if the owner wants one in the PDF, comes with the shift report (P2.11, ADR-0040).

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

As built (slice 4): `POST /api/v1/verification/items/{itemId}/correct` (`verification.items.review`, `If-Match`, a
recent sign-in checked after scope and separation of duties) takes `fields` and a reason of 10 to 500 characters. In
one transaction, Verification calls the record type's `IReviewableRecords.CorrectAsync` and ledgers
`verification.review_item_corrected.v1` (both versions, both hashes, every field before and after, the reason). For
shift entries FieldOps corrects the meter values and the fuel levels (a changed machine or shift implies another meter
and is left to a later slice): a new version with the corrected values, `corrected_by` and `correction_reason`, no
envelope and no device (empty IDs, the decision is Verification's), a hash of its canonical values; it then
re-evaluates the meter's continuity and the entry's Δ flags on the corrected values. The change is material when the
Δ moves by more than 2 h on an hour meter or by more than 5 % of the signed Δ. The corrected item is `corrected`; the
new version's item is approved by the corrector, or `awaiting_second` when material, approved by
`POST …/second-approve` (`verification.items.second-approve`, privileged: Tenant Admin, Site Manager, a recent sign-in,
never the corrector: `403 verification.sod_own_correction`), ledgered `verification.review_item_second_approved.v1`.
A second refusal is a later slice.

As built (slice 5): `review_policies` keeps every published version per record type (append-only, RLS, one row per
version: two administrators publishing at once, one wins). `PUT /api/v1/verification/policies/{recordType}`
(`verification.policies.manage`, privileged: Tenant Admin, a recent sign-in) publishes the next version from now or a
later date, never earlier: auto-approval, the four-eyes hours (0 to 24, one decimal) and share (0 to 1, two decimals),
the SLA hours (1 to 168) and the escalation role (site manager, tenant administrator, executive); ledgered
`verification.review_policy_published.v1`. `GET /api/v1/verification/policies` (everyone who reads the queue) gives
the version in force and the history; without one, version 0, the defaults (no auto-approval, 2 h and 5 %, 24 h, the
site manager). Every item pins the version in force when it is created and its due time; an unflagged item under an
auto-approving policy is approved at once by `policy:<version>`, ledgered `verification.review_item_auto_approved.v1`
with the version and the person who published it (threat T-13). A correction is judged against the thresholds of the
item's own version, which Verification passes to the record type (`CorrectionThresholds`). The portal page is slice 5b.

As built (slice 5b): Settings › Review policies shows, per record type, the version in force (or the defaults) with
its date and publisher, a form (auto-approval, second approval beyond hours and a whole percentage of the signed Δ,
review within hours, escalation role) checked as the API checks it, and the history of published versions. Publishing
asks the person to confirm their identity and applies from now; someone who reads the queue sees it read-only.

As built (slice 6): `ReviewEscalationJob` (Workers, Quartz, every 5 minutes, one run at a time) runs
`EscalateOverdueReviews` company by company (a failing company is logged, event 7302, and the others run): an item
still pending or awaiting a second approval after its `dueAt` is escalated once, an `escalate` action by `sla` and
`verification.review_item_escalated.v1` naming the role of the policy version it started under. The due time is an
instant (creation plus the SLA hours), so the site's time zone does not change it; working hours are not counted (an
owner question if wanted). The queue carries `escalatedAt` and filters `?overdue=true`; it keeps paging by creation
order, so the console (P2.9) shows overdue items first through the filter rather than a re-sorted page.

As built (slice 7): settling is a flag's own lifecycle in Integrity. `POST /api/v1/integrity/flags/{id}/settle`
(reason and what happened, 1 to 500 characters) records a proposal on an open RULE-FO-01 flag
(`integrity.flag_settlement_proposed.v1`); `…/settlement/approve` by another person closes it as
`settled:<reason>` with `closedBy`; `…/settlement/decline` (anyone with the permission, the proposer withdrawing
included) clears the proposal and keeps the gap open (`integrity.flag_settlement_declined.v1`). All three need
`integrity.flags.settle` (site, privileged: Tenant Admin, Controller, Site Manager) and `If-Match`; proposing and
approving need a recent sign-in, declining does not (it hides nothing). Decisions taken while building:
- **Every reason needs the second person.** A data-entry error is not settled by hand: `settle` refuses it
  (422 `integrity.correct_the_entry`) and the entry is corrected through the review (§1), which runs continuity again
  and closes the flag as `re-evaluated`. Otherwise one person could hide a real gap by calling it a typing error.
- **A settled gap is never raised again.** `IIntegrityFlags.SyncAsync` skips a key whose flag was closed as
  `settled:…`, though the gap still holds; a new gap is a new key.
- **"Unexplained" raises RULE-FO-11 at the proposal**, not at the approval: suspicion does not wait for the second
  person, and declining does not withdraw it. RULE-FO-11 is critical, a security rule (cannot be switched off), on the
  gap's meter, keyed by the gap's flag ID (`cause`: `unexplained` or `unsettled_48h`), and is never closed by a check.
- **48 h:** `UnsettledGapsJob` (Workers, every 15 minutes) raises RULE-FO-11 on every gap still open 48 h after it
  was opened, a proposal waiting or not; `fraud_suspected_at` on the gap makes it once per gap.
- The flag row's trigger now lets an open flag change its closing, settlement and `fraud_suspected_at` (once) fields
  only; a closed flag still never changes. Flags carry `xmin` as their version: a save that loses a race is a 409, and
  an upload closing the same flag at the same moment fails and is retried by the tablet.
- Separation of duties against the gap's own operators was left to the close-out; slice 8 added it (below).


As built (slice 8, close-out): neither the person who proposes a settlement nor the one who approves it may have
signed one of the gap's entries. Integrity asks Identity's `IReviewers` for the caller's linked field account and the
new `IFlagParticipants` contract (FieldOps: any version of the entries the flag relates to) whether that person
signed one (403 `integrity.sod_own_gap`). Demo step 4 runs end to end in `DemoStepFourTests` (host): a shift signed on
an enrolled tablet, synced, queued, corrected beyond the threshold, refused to the corrector, approved by a site
manager, and the ledger's before → after with both hashes; the entry's third version names the corrector.

## 9. Security review (P2.8 close-out, 2026-11-24)

| Threat | What P2.8 does | Evidence |
|---|---|---|
| T-07 repudiation | Every decision is an append-only `review_actions` row and a ledger entry under the authenticated person (`actor`), bound to the record's version and hash; a correction ledgers each field's before → after and both hashes; bulk approval ledgers one batch naming every item and hash; auto-approval names the policy version and its publisher; settlements ledger proposal, approval or decline with the person. No device-like signature for portal users (§3, option 3): the hash-chained ledger is the proof | `ReviewDecisionsTests`, `ReviewPoliciesTests`, `DemoStepFourTests`, `FlagsTests.Settlement` |
| T-13 own entries | SoD on the server at action time, never from the client: the reviewer as a person (office account → linked field account) against the record's producer and anyone who drove the asset during the entry (approve, reject, correct, bulk: each item); the corrector never second-approves; a settler or approver of a gap never signed one of its entries; the proposer never approves their own settlement | TC-P2-VER-001, 002, 003, 009; `A_person_who_signed_one_of_the_gaps_entries_neither_settles_nor_approves_it` |
| T-13 permission removed | Permissions and site scopes are read on each request (TC-P2-VER-009): a reviewer who loses the role or the site gets 403/404 at the next action | `ReviewDecisionsTests` |
| T-13 privileged edits | Second approval, review policies, field-account links and settlements are privileged permissions (second factor) with a recent sign-in inside the handler, after the scope check, so another company's item is a 404 and not a 401; policies are versions, never edited, each ledgered with its publisher; a link change is ledgered | `ReviewPoliciesTests` (TC-P2-VER-007), Identity `LinkFieldAccount` tests, isolation suite |
| Bulk | All or nothing: 1–100 distinct items, each in scope, at its version, pending and unflagged; flagged entries are decided one by one | TC-P2-VER-002 |
| Hiding a gap | Two people for every reason; a data-entry error is corrected, not settled; "unexplained" or 48 h raises RULE-FO-11 (security rule, cannot be switched off, never closed by a check); a settled key is not raised again but every step is in the ledger | `FlagsTests.Settlement` |
| T-09/T-11 isolation | RLS on every Verification and Integrity table; the isolation suite attacks `itemId`, `recordType` and `flagId` with tenant A's values and looks for A's hashes and IDs | `TenantIsolationSuite` (549/549) |
| Concurrency | Items and flags are versioned (`xmin`, `If-Match`): two reviewers at once, one wins, the other gets 409 | TC-P2-VER-008 |
| Residual | SoD sees a controller who also drives only when the company links the two accounts (§4); an unlinked controller could approve their own field work. Recorded as **R-15**; the users page shows the link and P2.9's console warns when a reviewer of a site has no link while a field account with the same name exists. Approve and reject need no recent sign-in (a session is enough); the ledger binds them to the person | threat model R-15 |

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

---
title: "Phase 2 — Field Operations, Verification & Signatures"
area: phases
tags: [phase-2, shifts, sync, signatures, verification]
source: "PHASES_DETAILED_PLAN.md §9"
status: baseline
last_updated: 2026-10-21
---

# Phase 2 — Field Operations, Verification & Signatures

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 6 weeks (W9–W14), 3 sprints · **Team:** full core team

## 9.1 Objectives & business value

Deliver the **anti-fraud core**: trusted, signed, verified shift data captured offline. This is the heart of the product and the basis of every later number (maintenance, cost, fuel efficiency).

## 9.2 Scope

**In:** shift lifecycle, meter readings with in-app photos, fuel gauge level, pre-start checklists, outbox sync with blobs, signatures (handwritten + device ECDSA + server countersignature), generic verification workflow (approve/reject/correct, SoD, four-eyes, escalation), inter-shift continuity & exceptions, meter replacement/failure events, clock-skew & geofence flags, controller console, shift report PDF, QR verification page, notifications foundation (in-app).
**Out:** fuel dispensing (P3), tickets (P3), RUL calculations (P4).

## 9.3 Epics

| Epic | Title |
|---|---|
| EP-P2-01 | Shift lifecycle & field capture |
| EP-P2-02 | Offline outbox & sync protocol |
| EP-P2-03 | Electronic signatures |
| EP-P2-04 | Verification workflow engine |
| EP-P2-05 | Continuity, meter events & integrity flags |
| EP-P2-06 | Controller console |
| EP-P2-07 | Documents & public verification |
| EP-P2-08 | Notifications foundation |

## 9.4 User stories & acceptance criteria

### EP-P2-01 Shift lifecycle & field capture

**US-P2-FO-01 — Start a shift on an asset** · *Persona: Field Operator* · Must · M
As an operator, I want to start my shift on my machine in under 60 seconds, so that my hours are recorded without paperwork.
- AC1: Given I am unlocked on an enrolled tablet, when I scan the asset QR (or pick from my site's list), then the current shift instance (by site shift pattern and local time) is preselected.
- AC2: I must capture a **photo of the meter** using the in-app camera (gallery upload is disabled), then enter the start meter value. The previous end value is shown as reference.
- AC3: I record fuel gauge level (0–100 % slider with ¼ marks) and optional remarks (voice-to-text allowed).
- AC4: Validation: start value ≥ last known value of that meter (unless a meter event exists); unusual jump (> configurable max hours since last end) shows a warning requiring a reason.
- AC5: GPS position and accuracy are captured (if permission granted); outside site geofence → flag (not blocking).
- AC6: Works offline; the entry is saved locally as `Draft` until signed.
- AC7: Median completion time ≤ 60 s in usability test.

**US-P2-FO-02 — Pre-start checklist** · *Persona: Field Operator* · Must · M
- AC1: If the asset type has an active checklist template, it is required before shift start (configurable: required / optional).
- AC2: Items: OK / Not OK / N/A, photo required on Not OK for critical items.
- AC3: A Not OK on a critical item (e.g., brakes) marks the asset "unsafe – do not operate" and notifies the site manager and maintenance. Operator cannot start the shift on that asset unless a manager overrides (override audited).

**US-P2-FO-03 — Checklist template builder** · *Persona: Site Manager* · Should · M
- AC1: Build templates per asset type with items, criticality, photo requirement, FR/EN text.
- AC2: Templates versioned; submissions keep template version.

**US-P2-FO-04 — End a shift** · *Persona: Field Operator* · Must · M
- AC1: From "My active shift", I capture the end meter photo + value, fuel level, stoppage summary (optional reason codes: breakdown, weather, no material, no operator, other).
- AC2: System computes ΔH (or ΔKM); if ΔH > shift duration + tolerance, or negative, it blocks submission with an explanation (unless meter event).
- AC3: Summary screen shows start/end/Δ values before signing.

**US-P2-FO-05 — Multiple assets / operator change during shift** · Should · M
- AC1: An operator can end their segment and another operator can continue on the same asset in the same shift (handover). Each segment is a separate entry with continuity enforced between segments.
- AC2: One operator can operate several assets sequentially in a shift; overlapping active segments for the same operator are rejected.

**US-P2-FO-06 — My history** · *Persona: Field Operator* · Should · S
- AC1: Operator sees their last 14 days of entries with status (Draft, Queued, Submitted, Approved, Rejected with reason, Corrected).
- AC2: Rejected entries can be re-opened as a new version with the rejection reason displayed.

### EP-P2-02 Offline outbox & sync

**US-P2-OFF-01 — Outbox for signed commands** · *Persona: Engineer / Operator* · Must · L→split
- AC1: Signing an entry converts it into an immutable command in the outbox with `commandId` (UUIDv7), device sequence number, payload hash, envelope, and blob references.
- AC2: Outbox survives app kill, reboot, and service worker update (tested).
- AC3: Sync engine uploads in order per device, in batches (≤ 50 commands or 2 MB), with exponential backoff and jitter; triggers: app foreground, `online` event, every 2 min while online, manual "Sync now".
- AC4: The UI shows a persistent sync indicator: "All synced" / "N waiting" / "Error – tap for details".

**US-P2-OFF-02 — Blob upload (photos & signature images)** · Must · M
- AC1: Photos compressed client-side (max 1600 px long edge, quality tuned to ≈ 200 KB), SHA-256 computed before upload.
- AC2: Upload via pre-signed URL, resumable for files > 1 MB; server verifies hash; command referencing a missing blob is rejected with retry instruction.
- AC3: Synced blobs purged locally after 7 days (configurable) while keeping thumbnails for history.

**US-P2-OFF-03 — Server batch processing** · Must · M
- AC1: `POST /api/v1/sync/batches` (built in P1.15 with `accepted`, `duplicate`, `rejected`) returns per-command status: ACCEPTED, DUPLICATE (same commandId), REJECTED (validation, reason code), CONFLICT (business conflict creates exception, still stored).
- AC2: Device sequence gaps are detected and reported (`MISSING_SEQUENCE`), and the device re-sends missing commands.
- AC3: Processing is idempotent: replaying the same batch 10 times creates exactly one record per command.
- AC4: Batch processing p95 < 1 s for 50 commands.

**US-P2-OFF-04 — Status pull** · Must · S
- AC1: Devices pull status changes of their users' submissions (approved, rejected, corrected) and display them.
- AC2: Push notification (Web Push, when supported) informs operator of rejection.

**US-P2-OFF-05 — Clock offset measurement** · Must · S
- AC1: Each successful sync measures device-server offset; stored per device.
- AC2: Entries whose device time deviates from expected (receipt time minus offline duration window, plus offset) by more than the tolerance get the `CLOCK_SKEW` flag.

### EP-P2-03 Electronic signatures

**US-P2-SIG-01 — Handwritten signature capture** · *Persona: any signer* · Must · M
- AC1: Signature pad captures vector strokes (pressure if available) on a full-width canvas; "Clear" and "Confirm" buttons; minimum stroke length validation (rejects dots).
- AC2: The signer's name, role, date/time and the summary of what is signed are shown above the pad ("I confirm the values above").
- AC3: Strokes stored as JSON + rendered PNG; both hashed.

**US-P2-SIG-02 — Cryptographic device signature** · Must · M
- AC1: The envelope ([Trust Layer: Verification, Signatures & Audit Ledger §13.2](../../02-architecture/trust-layer-signatures-audit.md#132-signature-envelope)) is built over the RFC 8785 canonical payload; device key (ECDSA P-256, non-extractable) signs the payload hash.
- AC2: Signing works offline.
- AC3: Server verifies device signature against the enrolled public key; invalid signature → command REJECTED (`INVALID_SIGNATURE`) and security event raised.

**US-P2-SIG-03 — Server countersignature** · Must · M
- AC1: On acceptance, the server countersigns `{envelope hash, receivedAt}` using a KMS key (key never leaves KMS).
- AC2: Key rotation supported (kid in envelope); old signatures remain verifiable.

**US-P2-SIG-04 — Signature verification API** · Must · S
- AC1: `GET /api/v1/trust/envelopes/{id}/verification` returns: payload hash match, device signature valid, server countersignature valid, ledger entry present & chain valid, timestamp token (if any).
- AC2: Verification is shown as a badge on every signed record in the portal ("Verified ✔" with details).

### EP-P2-04 Verification workflow

**US-P2-VER-01 — Review queue** · *Persona: Shift Controller* · Must · M
As a controller, I want a queue of entries waiting for my review, so that I can validate the shift quickly.
- AC1: Queue lists entries from my scope (sites) with status PendingReview, sorted by risk (flags first) then age.
- AC2: Filters: site, shift, asset, operator, flag type, date.
- AC3: Each row shows key values, Δ, flags (continuity, skew, geofence, unusual jump), photo thumbnails.
- AC4: Queue updates in near-real-time (≤ 10 s) when new entries sync.

**US-P2-VER-02 — Approve** · Must · S
- AC1: Controller approves single or multiple entries (bulk approve allowed only for entries without flags).
- AC2: Approval requires controller signature (one signature can cover a bulk approval, with the list of entry hashes in the envelope).
- AC3: SoD: approving own entry, or an entry for an asset the controller operated in the same shift, is blocked.

**US-P2-VER-03 — Reject** · Must · S
- AC1: Reject requires a reason code + comment; operator notified.
- AC2: Rejected entry stays in history; resubmission creates a new version linked to it.

**US-P2-VER-04 — Correct with full before/after trail** · Must · M
- AC1: Controller can correct specific fields (meter values, fuel level, shift, asset) with a mandatory reason; the system shows before → after.
- AC2: Original entry status becomes `CORRECTED`; a new version holds corrected values; both are linked; ledger records both hashes and the diff.
- AC3: Corrections beyond thresholds (e.g., meter change > 2 h or > 5 %) require **second approval** by a different user with the `verification.second-approve` permission.

**US-P2-VER-05 — Review policies** · *Persona: Tenant Admin* · Must · M
- AC1: Configure per record type: who reviews (role + scope), auto-approval rules (e.g., no flags and Δ within norms → auto-approve, still auditable), four-eyes thresholds, SLA hours, escalation target.
- AC2: Policy changes are versioned and audited; each review item records the policy version used.

**US-P2-VER-06 — SLA & escalation** · Must · S
- AC1: Items pending beyond SLA escalate to the escalation role and appear red in queue.
- AC2: Daily digest to site manager: pending count, overdue count, corrections count.

### EP-P2-05 Continuity & meter events

**US-P2-FO-07 — Inter-shift continuity check** · Must · M
- AC1: When a start reading is accepted, the server compares it with the last accepted end reading of the same meter. If they differ and no meter event explains it → `ContinuityException` created and linked to both entries.
- AC2: Continuity is evaluated on server receipt order **and** re-evaluated when late-arriving offline entries change the sequence (ordering by shift time, not receipt time).
- AC3: Exception appears in controller queue with the two photos side by side.

**US-P2-FO-08 — Resolve continuity exception** · *Persona: Shift Controller* · Must · M
- AC1: Resolution reason codes: data entry error (with correction), meter replaced, meter reset/rollover, maintenance run between shifts, towing/transport, unexplained.
- AC2: "Unexplained" or unresolved > 48 h → `FRAUD_SUSPECTED` flag visible on the integrity dashboard (P3) and notification to executives.
- AC3: Resolution requires signature; reasons other than data-entry error require second approval.

**US-P2-AST-01 — Meter replacement / reset event** · *Persona: Maintenance / Site Manager* · Must · M
- AC1: Record meter replaced: old meter final reading, new meter initial reading, date/time, photo of both, reason. Accumulated usage remains continuous using offset.
- AC2: Record reset/rollover similarly.
- AC3: Event is signed and approved like other records.

**US-P2-AST-02 — Meter failure (fallback switch – data side)** · Must · S
- AC1: Authorized manager marks meter as FAILED with date and reason; field app then asks operators for **operating hours estimate** (start/end time of operation) instead of meter values for that asset.
- AC2: Entries for failed meters are tagged `ESTIMATED`; RUL engine in P4 uses them.
- AC3: Repairing the meter (new reading) closes the failure period.

**US-P2-INT-01 — Integrity flags engine (v1)** · Must · M
- AC1: Rules implemented: RULE-FO-01 continuity mismatch, RULE-FO-02 negative/oversized Δ, RULE-FO-03 clock skew, RULE-FO-04 outside geofence, RULE-FO-05 photo missing EXIF/in-app marker, RULE-FO-06 same photo hash reused, RULE-FO-07 entry from revoked device.
- AC2: Each flag has severity, explanation text (FR/EN), and link to evidence.
- AC3: Rules have tenant-configurable thresholds and on/off switches (except security rules).

### EP-P2-06 Controller console

**US-P2-VER-07 — Fast review UX** · *Persona: Shift Controller* · Must · M
- AC1: Keyboard shortcuts (A approve, R reject, C correct, J/K next/prev) on desktop; swipe actions on tablet.
- AC2: Photo viewer with zoom, side-by-side previous/current meter photos.
- AC3: Target: a controller reviews 50 unflagged entries in < 5 min (usability test).

**US-P2-VER-08 — Shift overview board** · *Persona: Site Manager / Controller* · Should · M
- AC1: Per shift: assets planned vs. started vs. ended, missing end entries, pending reviews, flags.
- AC2: "Missing end" alert 1 h after shift end.

### EP-P2-07 Documents & public verification

**US-P2-SIG-05 — Signed shift report PDF** · Must · M
- AC1: Per shift and site: list of entries with values, Δ, operator, controller, flags, signatures images, verification QR.
- AC2: PDF itself carries a document hash in the footer; the QR links to verification page.
- AC3: Generated in < 10 s for 200 entries.

**US-P2-SIG-06 — Public verification page** · Must · S
- AC1: QR opens `https://verify.karry.app/<envelopeId>#<token>` showing: document type, date, issuing company name, verification status, hash, without personal data beyond names allowed by tenant policy.
- AC2: Rate-limited; no enumeration (unguessable token).

### EP-P2-08 Notifications foundation

**US-P2-NOT-01 — In-app notification center** · Must · M
- AC1: Notification center in portal and field app; read/unread; deep links.
- AC2: Notification rules framework (event → audience by role/scope → channels), with preferences per user.
- AC3: Email channel for digests.

## 9.5 Technical scope

**Backend modules:** `FieldOps` (Shift, ShiftEntry, Checklist), `Verification` (ReviewItem, ReviewPolicy), `Trust` (SignatureEnvelope, verification, countersigning), `Assets` (meter events), `Notifications`, `Sync` building block (batch processor, sequence tracking, idempotency, blob registry).

**Data model additions**

```
fieldops.shift_instance(id, tenant_id, site_id, pattern_id, starts_at, ends_at, label)
fieldops.shift_entry(... see master plan §11.3 ..., version int, root_id uuid, segment_no int, estimated bool, flags text[])
fieldops.checklist_template(id, tenant_id, asset_type, version, items jsonb, active)
fieldops.checklist_submission(id, tenant_id, entry_id, template_version, answers jsonb, unsafe bool)
verification.review_item(id, tenant_id, record_type, record_id, status, policy_version, assigned_scope, due_at, escalated_at)
verification.review_action(id, tenant_id, review_item_id, action, actor_id, reason_code, comment, diff jsonb, envelope_id, at)
verification.review_policy(id, tenant_id, record_type, version, rules jsonb, effective_from)
fieldops.continuity_exception(id, tenant_id, meter_id, prev_entry_id, next_entry_id, delta, status, resolution_code, resolved_by, resolved_at)
assets.meter_event(id, tenant_id, meter_id, type[REPLACED|RESET|FAILED|REPAIRED], old_final, new_initial, offset, at, envelope_id)
trust.signature_envelope(id, tenant_id, record_type, record_id, payload_hash, signer_id, device_id, device_sig, server_sig, kms_kid, received_at, strokes_key, image_hash, tst bytea null)
sync.device_sequence(device_id, last_seq), sync.processed_command(command_id, status, result jsonb)
integrity.flag(id, tenant_id, rule_code, severity, record_type, record_id, details jsonb, status)
```

**Key APIs**

| Method | Route |
|---|---|
| POST | `/api/v1/sync/batches` |
| POST | `/api/v1/sync/blobs/presign` |
| GET | `/api/v1/sync/changes` (reference data since P1.15; submission statuses added) |
| GET | `/api/v1/verification/queue` |
| POST | `/api/v1/verification/items/{id}/approve` · `/reject` · `/correct` · `/second-approve` |
| CRUD | `/api/v1/verification/policies` |
| GET/POST | `/api/v1/fieldops/continuity-exceptions` · `/{id}/resolve` |
| POST | `/api/v1/assets/meters/{id}/events` |
| CRUD | `/api/v1/fieldops/checklist-templates` |
| GET | `/api/v1/fieldops/shifts/{id}/report.pdf` |
| GET | `/api/v1/trust/envelopes/{id}/verification` |
| GET | `/verify/{envelopeId}` (public) |

**Frontend:** field app flows (start/end shift, checklist, signature, history, sync center); portal (review queue, item detail, correction dialog, exceptions, policies, shift board, templates, notification center).

## 9.6 Test plan (Phase 2)

**Functional & integration**

| Test ID | Description | Expected |
|---|---|---|
| TC-P2-FO-001 | Start shift with QR scan offline | Draft saved, preselected shift correct for local time incl. night shift crossing midnight |
| TC-P2-FO-002 | Gallery upload attempt for meter photo | Not possible |
| TC-P2-FO-003 | Start value lower than previous end | Blocked with explanation |
| TC-P2-FO-004 | Checklist critical Not OK | Asset unsafe, shift start blocked, notifications sent |
| TC-P2-FO-005 | ΔH > shift duration + tolerance | Submission blocked |
| TC-P2-FO-006 | Handover between operators | Two segments, continuity enforced |
| TC-P2-FO-007 | Night shift start ≠ day shift end | Continuity exception created |
| TC-P2-FO-008 | Late offline entry arrives after next shift already synced | Continuity re-evaluated in shift order; correct exception |
| TC-P2-FO-009 | Meter replaced event then next reading | No exception, accumulated usage continuous |
| TC-P2-FO-010 | Meter failed → estimated hours entries | Tagged ESTIMATED |
| TC-P2-SYNC-001 | Replay same batch 10× | Exactly one record per command |
| TC-P2-SYNC-002 | Kill app mid-upload | Resumes, no duplicates |
| TC-P2-SYNC-003 | Sequence gap simulation | MISSING_SEQUENCE then recovery |
| TC-P2-SYNC-004 | Blob missing on command | Rejected with retry; succeeds after blob upload |
| TC-P2-SYNC-005 | Blob hash mismatch (corrupted) | Rejected, security event |
| TC-P2-SYNC-006 | 72 h offline, 400 entries, 300 photos, then 3G throttled | All synced < 15 min, 0 loss |
| TC-P2-SYNC-007 | Service worker update while outbox non-empty | Outbox preserved |
| TC-P2-SYNC-008 | Device clock set +3 h | CLOCK_SKEW flag |
| TC-P2-SIG-001 | Valid device signature | Accepted, countersigned |
| TC-P2-SIG-002 | Payload modified after signing | INVALID_SIGNATURE |
| TC-P2-SIG-003 | Signature from revoked device key | Rejected/quarantined |
| TC-P2-SIG-004 | KMS key rotation | Old & new signatures verify |
| TC-P2-SIG-005 | Public verification page enumeration attempt | Rate-limited, no data |
| TC-P2-VER-001 | Controller approves own entry | Blocked (SoD) |
| TC-P2-VER-002 | Bulk approve with a flagged entry included | Flagged excluded / blocked |
| TC-P2-VER-003 | Correction beyond threshold | Requires second approver ≠ first |
| TC-P2-VER-004 | Correction trail | Before/after diff in ledger, both versions visible |
| TC-P2-VER-005 | SLA breach | Escalated, red in queue, digest includes it |
| TC-P2-VER-006 | Auto-approval policy | Unflagged entries auto-approved, audited with policy version |
| TC-P2-VER-007 | Policy change mid-day | Items keep their policy version |
| TC-P2-INT-001..007 | Each RULE-FO-0x positive & negative case | Correct flags |
| TC-P2-DOC-001 | Shift PDF 200 entries | < 10 s, QR verifies |

**Property tests:** continuity engine (random sequences of readings/events → exceptions exactly when unexplained discontinuities exist); ΔH computation never negative without meter event.

**Performance:** k6 simulation of **shift change burst**: 300 devices each uploading 10 commands + 10 photos within 15 min. p95 batch < 1 s, no errors, queue update ≤ 10 s.

**Device lab:** 3 low-end Android tablets + 1 mid-range phone; sunlight readability test outdoors; glove test; battery drain over a 12 h shift with app open (< 15 % attributable).

**Security:** threat model review of sync & signatures; attempt replay with modified payload; attempt to forge device registration; attempt cross-tenant blob access via pre-signed URL reuse (must fail: URL scoped to tenant + object key + expiry).

**UAT (pilot site, staging):** 1 day shadow run: operators use Karry in parallel to paper for 2 shifts on 5 machines; controller reviews; compare paper vs Karry values; collect feedback.

## 9.7 Deliverables

- Field capture flows (shift, checklist, signature, sync center).
- Verification engine + controller console + policies.
- Continuity & integrity flags v1.
- Signature envelopes, countersigning, public verification.
- Shift report PDF.
- Notification center.
- Help articles FR/EN: "Start/End a shift", "Review entries", "Resolve continuity exceptions".

## 9.8 Demo script

1. Tablet in airplane mode: operator does pre-start checklist, starts shift with meter photo, signs.
2. Second operator does night shift start with a **wrong** meter value; signs.
3. Reconnect: both sync; controller queue shows the continuity exception with side-by-side photos.
4. Controller corrects the value (beyond threshold) → second approver approves → ledger diff shown.
5. Generate shift PDF → scan QR with a phone → verification page shows "Verified".
6. Tamper attempt (modified payload via API) → rejected with INVALID_SIGNATURE.

## 9.9 Exit gate

- [ ] Shadow run completed with ≥ 95 % value agreement between paper and Karry (differences explained).
- [ ] Shift-change burst test passed.
- [ ] 72 h offline test passed with zero loss.
- [ ] Security review of signatures & sync: no high findings open.
- [ ] Controller usability target met (50 entries < 5 min).

## 9.10 Risks

| Risk | Mitigation |
|---|---|
| Operators resist photo requirement | Make photo fast (auto-focus, one tap); explain purpose; manager support |
| Continuity exceptions overwhelm controllers at start | Tune thresholds during shadow run; exceptions grouped per asset |
| WebCrypto keys lost when users clear browser data | Re-enrol flow; server countersignature still proves receipt; clearing data logged when detected |

## 9.11 Commercial outcome

Core differentiator demonstrable. First recorded demo video for marketing ("From paper to verified shift in 60 seconds").

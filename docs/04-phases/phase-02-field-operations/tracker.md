---
title: "Phase 2 Tracker: Sub-Phases, Carry-Overs, Evidence & Exit Gate"
area: phases
tags: [phase-2, tracker, status, sub-phases, exit-gate, carry-overs]
source: "Phase 2 execution"
status: draft
last_updated: 2026-11-23
---

# Phase 2 Tracker: Sub-Phases, Carry-Overs, Evidence & Exit Gate

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 2 plan](plan.md) · [Sub-phases](sub-phases.md)

Live status of [Phase 2](plan.md), executed in the order of the [sub-phase plan](sub-phases.md). Update this file when
a sub-phase starts, when evidence is produced (PRs, CI run IDs, measurements), and when it finishes.

Legend: ✅ done · 🟡 in progress · ⏳ waiting on owner or external input · ⬜ not started

## 1. Sub-phases

| # | Sub-phase | Status | Evidence (PRs, CI runs, measurements) / next action |
|---|---|---|---|
| P2.0 | Entry, decisions & test data | ✅ | ADRs [0031](../../adr/0031-sync-command-processing.md), [0032](../../adr/0032-evidence-blobs.md), [0033](../../adr/0033-signature-envelopes-and-server-keys.md) proposed; versions in [technology stack §7.1.2](../../02-architecture/technology-stack.md#712-verified-versions-phase-2-start-2026-11-09) (**PDFsharp 6.5 is AGPL**: pin 6.2.4 MIT or another engine, P2.11 spike); shift data generator `tools/demo/generate_shifts.py` (12 tests, CI `tooling`); Phase 2 test strategy in [testing strategy](../../05-quality/testing-strategy.md#phase-2-additions-field-operations-p20). PR: docs/phase-2-entry |
| P2.1 | Sync command processing | ✅ | Slice 1 ✅ #148 (handlers in the upload: one transaction, lock first, savepoint per command, `conflict`/`type_unknown`/`handler_failed`, `serverTime`; TC-P2-SYNC-009, 013). Slice 2 ✅ #149 (waiting commands, `missingSequences`, `sync.too_far_ahead`; TC-P2-SYNC-003). Slice 2b ✅ #150 (gap job, gauges, alert `KarrySyncGapOpen`, runbook). Slice 3 ✅ #151 (status pull `submissions`, server-measured clock offset, waiting commands keep their arrival). ADR-0031 accepted ([§8 as built](../../adr/0031-sync-command-processing.md#8-as-built-p21-2026-11-09)) |
| P2.2 | Field outbox, upload & evidence blobs | ✅ | Spike ✅ #152: SeaweedFS honours `x-amz-checksum-sha256` on a pre-signed `PUT` (`400 BadDigest`, signed header, expiry, `HEAD` checksum mode), so the store-side check is used ([ADR-0032 §2](../../adr/0032-evidence-blobs.md#2-upload-by-pre-signed-put-with-the-checksum-the-store-verifies)). Slice 1 (presign endpoint `POST /sync/blobs/presign`, `IEvidenceStore`, `Storage:Evidence`; TC-P2-SYNC-012 server side; [§7 as built](../../adr/0032-evidence-blobs.md#7-as-built-p22-2026-11-09)) ✅ #153. Slice 2 (evidence bucket created with Object Lock, compliance 5 years, and a CORS rule for the field app's origins; `EvidenceStoreTests`) ✅ #154. Slice 3 (blob check before a command is handled: `sync.blob_missing` retryable, `sync.blob_mismatch` + security event, `sync.evidence_invalid`; registry `sync.command_evidence`; TC-P2-SYNC-004/005 server side) ✅ #155. Slice 3b (ADR-0031 §3 amendment: a final rejection is kept in its place in the sequence, so it never blocks the tablet's later commands; `processed_commands.code`) ✅ #156. Slice 4 (`@karry/offline` schema v2: outbox API `enqueue`/`nextBatch`/`applyReceipts`/`applySubmissions`, evidence blobs with purge; TC-P2-SYNC-011 and 014 storage side) ✅ #157. Slice 5a (field app upload engine `pushOutbox` under a Web Lock, blobs before commands, `missingSequences`, pull stores `submissions`, `syncTablet` in the refresh loop) ✅ #158. Slice 5b (sync centre `OutboxStatus` in 5 languages, 2-minute trigger, backoff with jitter) ✅ #159. Close-out (metric `karry_sync_blobs_total{result}`, alerts `KarryEvidenceStoreUnavailable` and `KarrySyncBlobMismatch`, runbook [karry-evidence-upload](../../06-operations/runbooks/karry-evidence-upload.md), ADR-0032 accepted with its [security review](../../adr/0032-evidence-blobs.md#8-security-review-p22-close-out-2026-11-09)): PR from `feat/p2.2-close-out`. The browser E2E TC-P2-SYNC-002/007/011/014 moved to P2.5 (needs the entry screens) |
| P2.3 | Signature envelopes: handwritten, device, server | ✅ | Slice 1 (server key: `IServerSigner` port, OpenBao transit adapter verified on 2.7.2, rotation by `kid`; TC-P2-SIG-004 at adapter level; [ADR-0033 §6](../../adr/0033-signature-envelopes-and-server-keys.md#6-as-built-p23-2026-11-09)) ✅ #161. Slice 2 (OpenBao in the local stack: raft storage on a volume, `start.sh` init/unseal, key `karry-server`, signing-only token; smoke checks; [rotation runbook](../../06-operations/runbooks/karry-server-key-rotation.md)) ✅ #162. Slice 3a (envelope v1 in `@karry/crypto` and .NET `SignatureEnvelopes`, shared vectors from an independent implementation; cross-language test and TC-P2-SIG-002 at format level) ✅ #163. Slice 3b (envelopes stored with their ledger entry in the command's transaction: `IEnvelopeRecorder`, `audit.signature_envelopes` append-only with RLS; TC-P2-SIG-002, TC-P2-SIG-007) ✅ #164. Slice 3c (countersign job every 30 s in Karry.Workers, `audit.server_keys` and `audit.envelope_countersignatures` append-only, metrics, alerts `KarryCountersignLagHigh`/`KarryCountersignFailing`, [runbook](../../06-operations/runbooks/karry-signing-service-down.md); TC-P2-SIG-001, 004, 006, 007) ✅ #165. Slice 4 (verification endpoint `GET /api/v1/audit/envelopes/{id}/verification`: envelope, device signature, countersignature, ledger entry checked separately; TC-P2-SIG-001, 002) ✅ #166. Slice 5 (SignaturePad in `@karry/ui`, minimums, strokes JSON + PNG, `handwrittenHashes`; TC-P2-SIG-008) ✅ #167. Slice 6 (portal "Verified" badge `SignatureVerification`: status and every check, in the audit entry of a received signature; US-P2-SIG-04 AC2): ✅ #168. Close-out: [security review](../../adr/0033-signature-envelopes-and-server-keys.md#7-security-review-p23-close-out-2026-11-09) (revoked tablet now refused by the recorder too, TC-P2-SIG-003; residual R-12 key custody), ADR-0033 accepted; end-to-end browser test moved to P2.5: ✅ #169 |
| P2.4 | Shifts & shift entries (server) | ✅ | [ADR-0034](../../adr/0034-shift-entries.md) accepted. #170 shift resolution (TC-P2-FO-001 server part); #171 module `FieldOps`, append-only entries, start/end handlers with the envelope in the same transaction (TC-P2-FO-005, 010); #172 `RULE-FO-01`, `OVERLAP`, properties over 10 000 cases (TC-P2-FO-003, 006, 007, 011); #173 read API and permission `fieldops.entries.read`; close-out (PR from `feat/p2.4-location-close-out`): positions from the envelope, "done when" run `ShiftEntriesSyncTests` (generated shifts through sync, read back with the right shift, Δ and flags), three faults it found fixed, security review (T-01, T-10, new R-13) |
| P2.5 | Field capture flows (tablet) | 🟡 | Carries the browser E2E TC-P2-SYNC-002/007/011/014 moved from P2.2. Slice 1: shared validation vectors `tests/vectors/shift-entry-rules.json` read by the server (`ShiftEntryVectorsTests`) and the field app (`shiftRules.ts`, `shiftRules.test.ts`), TC-P2-FO-012: #175. Slice 2: in-app camera (`camera.ts`, `PhotoCapture.tsx`: back camera, tap to focus, ≤ 1600 px WebP/JPEG ≈ 200 KB, capture record, no gallery path; TC-P2-FO-002; [ADR-0032 §7](../../adr/0032-evidence-blobs.md#7-as-built-p22-2026-11-09)): #176. Slices 3–9 planned in [P2.5 slice plan](p2.5-capture-flows-plan.md). Slice 3a: meter states (FieldOps sync collection `meterStates`, Dexie v3, `meterState.ts` merging the tablet's own commands): #179. Slice 3b: shift resolution on the tablet (`shifts.ts`, shared vectors `tests/vectors/shift-resolution.json` read by `ShiftCalendarVectorsTests` and `shifts.test.ts`, TC-P2-FO-001 logic part; non-working days in the pull, Dexie v4): #182. Slice 4: sign and enqueue (`signShiftEntry.ts`: record checked, envelope with photo and handwriting, device signature, position, `captures`; 5 tests): #183. Slice 5a: start-shift flow (`StartShift.tsx`, `startRules.ts`; TC-P2-FO-001 and TC-P2-FO-003 on the tablet; pad and camera in lazy chunks, initial JS 213.7 KB gzip): #185. Slice 5b: drafts (Dexie v5, per operator, kept through an auto-lock, 24 h; US-P2-FO-01 AC6, T-01): #186. Slice 6: "My active shift", end-shift flow (Δ, stoppages, summary, sign) and handover (`previousRootId`): #188. Slice 7: "My history" (14 days, statuses, flags in words, refusal reason, thumbnails, start again after a refusal; US-P2-FO-06): #189. Slice 8a: browser E2E `field-shift.spec.ts` (offline start and end with fake-camera photos and drawn signatures, received once, envelope verified in the portal): #187. Slice 9a: security review ([ADR-0034 §10](../../adr/0034-shift-entries.md#10-security-review-p25-tablet-flows-2026-11-18): operator shown on the machine step, expired drafts purged at unlock), [lost-tablet runbook](../../06-operations/runbooks/karry-lost-tablet.md): #190. Slice 8b: `field-sync.spec.ts` (20 signed commands offline, two tabs uploading at once: TC-P2-SYNC-014): #192. Slice 8c: `field-sync-resume.spec.ts` (TC-P2-SYNC-002 app closed while the server stores a batch, TC-P2-SYNC-011 evidence kept until confirmed): #194. Slice 8d: `field-update.spec.ts` (TC-P2-SYNC-007, a new version applied with a full outbox): #195. Slice 8e: `field-key-rotation.spec.ts` (TC-P2-SIG-004: envelopes verified before and after a `karry-server` rotation): #196. Next: 9b (usability test, demo) |
| P2.6 | Pre-start checklists | ✅ | [Slice plan](p2.6-checklists-plan.md); [ADR-0035](../../adr/0035-pre-start-checklists-and-safety-holds.md) accepted (security review §7, residual R-14). Templates versioned per machine type (#199), portal builder (#200), signed submissions and safety holds with `ASSET_UNSAFE` and the ledgered release (#201), pull collections and shared answer vectors (#202), tablet checklist step and start block (#203), portal list and release of holds (#204), close-out: demo checklists (excavator optional, haul truck required) and the browser E2E `field-checklist.spec.ts` (TC-P2-FO-004, TC-P2-FO-014). |
| P2.7 | Meter events, continuity & integrity flags v1 | ✅ | [P2.7 slice plan](p2.7-integrity-plan.md), [ADR-0036](../../adr/0036-integrity-rules-and-flags.md) accepted (security review in it). Integrity module and flags #208; continuity engine RULE-FO-01/02 #209; meter events #210, #212; failed-meter mode #213; security rules FO-05/06/07 #215; clock skew and geofence FO-03/04 #216; rule settings per company #217; portal flag list and unique API schema names #218; planted-anomaly generator end to end #223; RULE-FO-09 signer check (R-13 mitigated) #220; RULE-FO-10 required checklist (R-14 mitigated) #221; security review #222; close-out (one-time contract exception emptied) in this PR |
| P2.8 | Verification engine | ⬜ | |
| P2.9 | Controller console | ⬜ | |
| P2.10 | Notifications foundation | ⬜ | |
| P2.11 | Shift report PDF & public verification | ⬜ | |
| P2.12 | Hardening, NFR, shadow run & exit gate M2 (**a** local/CI, **b** staging/device lab/pilot) | ⬜ | P2.12b needs the carry-overs of §2 |

## 2. Phase 1 carry-overs

Phase 2 started before the M1 gate could pass, because what blocks M1 is external ([entry decision](sub-phases.md#0-can-phase-2-start-entry-decision)).
These items **must be closed before P2.12b**.

| Item | Blocked on | Owner | Status |
|---|---|---|---|
| P1.18a hardening (slices 6b, 6 rest, 7) | — (agent work) | Agent | ✅ 2026-11-09 ([exit review draft](../phase-01-platform-foundation/exit-review.md)) |
| P1.15 real-tablet run (cold start, search timing, PIN unlock time, QR scan) | A low-end Android tablet | Owner | ⏳ |
| P1.17 environments & continuous delivery | Hosting provider & region (WP-P0-14) | Owner → agent | ⏳ |
| P1.18b staging hardening, restore drill, pilot data | P1.17 | Agent | ⏳ |
| M1 exit gate | All of the above | Owner + agent | ⏳ |

## 3. M2 exit gate

| Criterion (from [plan §9.9](plan.md#99-exit-gate) and [sub-phases P2.12](sub-phases.md#p212--hardening-non-functional-tests-shadow-run--exit-gate-m2)) | Status |
|---|---|
| Shadow run ≥ 95 % value agreement, differences explained | ⬜ |
| Shift-change burst test passed (staging) | ⬜ |
| 72 h offline test passed with zero loss | ⬜ |
| Security review of signatures & sync: no high finding open | ⬜ |
| Controller usability target met (50 entries < 5 min) | ⬜ |
| Phase 1 carry-overs closed; every Must story ✅; nightly green 7 days | ⬜ |

## 4. Decisions and findings

| # | Date | Decision or finding |
|---|---|---|
| 1 | 2026-11-04 | Phase 2 may start before M1 (blockers are external); P1.18a first; carry-overs gate P2.12b (D56) |
| 2 | 2026-11-04 | Plan amendments A1–A9 proposed ([sub-phases §6](sub-phases.md#6-amendments-to-the-phase-2-plan)); each takes effect with its ADR |

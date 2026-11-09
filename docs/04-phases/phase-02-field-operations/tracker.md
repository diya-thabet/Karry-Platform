---
title: "Phase 2 Tracker: Sub-Phases, Carry-Overs, Evidence & Exit Gate"
area: phases
tags: [phase-2, tracker, status, sub-phases, exit-gate, carry-overs]
source: "Phase 2 execution"
status: draft
last_updated: 2026-11-09
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
| P2.5 | Field capture flows (tablet) | 🟡 | Carries the browser E2E TC-P2-SYNC-002/007/011/014 moved from P2.2. Slice 1: shared validation vectors `tests/vectors/shift-entry-rules.json` read by the server (`ShiftEntryVectorsTests`) and the field app (`shiftRules.ts`, `shiftRules.test.ts`), TC-P2-FO-012: #175. Slice 2: in-app camera (`camera.ts`, `PhotoCapture.tsx`: back camera, tap to focus, ≤ 1600 px WebP/JPEG ≈ 200 KB, capture record, no gallery path; TC-P2-FO-002; [ADR-0032 §7](../../adr/0032-evidence-blobs.md#7-as-built-p22-2026-11-09)): PR from `feat/p2.5-camera`. Next: start-shift flow |
| P2.6 | Pre-start checklists | ⬜ | |
| P2.7 | Meter events, continuity & integrity flags v1 | ⬜ | |
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

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
| P2.2 | Field outbox, upload & evidence blobs | 🟡 | Spike ✅ #152: SeaweedFS honours `x-amz-checksum-sha256` on a pre-signed `PUT` (`400 BadDigest`, signed header, expiry, `HEAD` checksum mode), so the store-side check is used ([ADR-0032 §2](../../adr/0032-evidence-blobs.md#2-upload-by-pre-signed-put-with-the-checksum-the-store-verifies)). Slice 1 (presign endpoint `POST /sync/blobs/presign`, `IEvidenceStore`, `Storage:Evidence`; TC-P2-SYNC-012 server side; [§7 as built](../../adr/0032-evidence-blobs.md#7-as-built-p22-2026-11-09)) ✅ #153. Slice 2 (evidence bucket created with Object Lock, compliance 5 years, and a CORS rule for the field app's origins; `EvidenceStoreTests`): PR from `feat/p2.2-evidence-bucket`. Next: `HEAD` check when a command is handled (`sync.blob_missing`/`sync.blob_mismatch`), blob registry, tablet uploader |
| P2.3 | Signature envelopes: handwritten, device, server | ⬜ | |
| P2.4 | Shifts & shift entries (server) | ⬜ | |
| P2.5 | Field capture flows (tablet) | ⬜ | |
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

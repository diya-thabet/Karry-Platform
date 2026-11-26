---
title: "Phase 2 Exit Review (M2 Field Operations): Draft"
area: phases
tags: [phase-2, exit-review, m2, gate, evidence, hardening, demo]
source: "P2.12a slice 6 (hardening plan); exit gate in plan §9.9 and sub-phases §P2.12"
status: draft
last_updated: 2026-11-26
---

# Phase 2 exit review (M2): draft

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 2 plan](plan.md) · [Sub-phases](sub-phases.md) · [Tracker](tracker.md) · [Hardening plan](p2.12a-hardening-plan.md)

**Status: draft, no decision yet.** Written at the end of P2.12a (local stack and CI). Everything that can be proven
without hosting, tablets or a customer site is proven. The gate itself needs P2.12b: a shadow run at the pilot site,
the burst on staging, the usability tests with real operators and controllers, and the Phase 1 carry-overs, which all
wait on owner decisions (hosting provider, device lab, pilot site).

**Recommendation for now: _Go_ for P2.12b as soon as the owner items arrive; _No-go_ for declaring M2.** The field
workflow works end to end in CI, from an offline tablet to a verified PDF. The non-functional targets that can be
measured here are met. M2 is a promise about a real site with real paper to compare against, and that run has not
happened yet.

## 1. Exit gate checklist ([plan §9.9](plan.md#99-exit-gate), [tracker §3](tracker.md#3-m2-exit-gate))

| Box | Status | Evidence / what is missing |
|---|---|---|
| Shadow run ≥ 95 % value agreement, differences explained | ⬜ | Needs the pilot site and its paper forms (P2.12b, owner) |
| Shift-change burst test passed | 🟡 | **Passed in CI** (nightly `load`, 26 Nov 2026): 300 tablets, 3,000 signed commands and 9,000 blobs stored, 0 failed requests, p95 batch **752 ms** (< 1 s) on a 2-vCPU shared runner, outboxes drained in ≤ 1 s, ledgers intact. Found and fixed on the way: the per-company ledger lock held for a whole batch (L-5, ADR-0008 amendment). **Staging run** waits for P1.17 |
| 72 h offline test passed with zero loss | ✅ (CI) | Nightly `offline` (TC-P2-SYNC-006, #274): 200 shifts (400 signed commands with photos) over a faked 72 h, then a 3G network: **all sent in 4.5 min** (limit 15), none refused, all 200 in the history. A real low-end tablet repeats it in the device lab (P2.12b) |
| Security review of signatures & sync: no high finding open | ✅ | [Security review](p2.12a-security-review.md) (#275): every attack is a CI test; S-4 fixed. ZAP baseline on the public page (#276): no high, the only medium accepted (W-2). Residual R-16 waits for the deployment |
| Controller usability target met (50 entries < 5 min) | ⬜ | The console is built for it (keyboard, bulk approve, P2.9); the timed test with a controller is an owner item |
| Phase 1 carry-overs closed; every Must story ✅; nightly green 7 days | ⬜ | Carry-overs wait on hosting and tablets ([tracker §2](tracker.md#2-phase-1-carry-overs)). Must stories: [§2](#2-must-stories). Nightly: the new `load` and `offline` jobs passed for the first time on 26 Nov 2026; seven green nights start counting from the merge |

## 2. Must stories

The 27 Must stories of the [plan](plan.md) are built and tested in CI; the sub-phase evidence is in the
[tracker](tracker.md#1-sub-phases).

| Area | Must stories | Status |
|---|---|---|
| Field capture | US-P2-FO-01, 02, 04 | ✅ built, E2E in CI; FO-01/04 wait on the operators' usability test (P2.5 slice 9b, owner) |
| Continuity | US-P2-FO-07, 08 | ✅ |
| Offline and sync | US-P2-OFF-01…05 | ✅ (72 h test above) |
| Signatures | US-P2-SIG-01…06 | ✅ (PDF and public page: P2.11) |
| Verification | US-P2-VER-01…07 | ✅ built; VER-07's timed target is the controller usability test |
| Meters | US-P2-AST-01, 02 | ✅ |
| Integrity | US-P2-INT-01 | ✅ |
| Notifications | US-P2-NOT-01 | ✅ |

## 3. Demo script rehearsal ([plan §9.8](plan.md#98-demo-script))

`tools/e2e/run.sh pnpm --filter @karry/e2e demo` runs the six steps in one browser run on the local stack (the PR
job `local-stack` runs the same specs with the rest of the suite).

| Step | What it shows | Spec |
|---|---|---|
| 1 | Offline: pre-start checklist, shift started with a meter photo, signed | `field-checklist.spec.ts` (checklist, safety hold), `field-shift.spec.ts` (shift with photos and signatures offline) |
| 2 | A second operator starts with a wrong meter value and signs | `review-queue.spec.ts` (the second shift a tenth of an hour above the first's end) |
| 3 | Back online: the queue shows the continuity exception with the photos side by side | `review-queue.spec.ts` (TC-P2-VER-010) |
| 4 | The controller corrects beyond the threshold, a second person approves, the ledger keeps before and after | `demo.spec.ts` (TC-P2-VER-003) |
| 5 | Shift PDF, its QR opens "Verified" without sign-in | `shift-report.spec.ts` (TC-P2-DOC-001, 002) |
| 6 | A payload changed after signing is refused: `sync.signature_invalid`, and the tablet says why | `demo.spec.ts` (TC-P2-SIG-002) |

## 4. What hardening found (P2.12a)

| # | Finding | Fix |
|---|---|---|
| L-5 | A tablet's batch held its company's ledger head for its whole length: at a shift change, every other upload of the company waited (10-15 s per batch) | Ledger entries of a sync batch appended at commit time, still in the same transaction (`LedgerDeferral`, ADR-0008 amendment, #273) |
| — | The field app could stay on "Mettre à jour" after an update: the reload timer started only once `skipWaiting` resolved | Timer started first (#273) |
| L-7 | The burst stopped at the 102nd machine: the demo companies are on the trial plan | Demo seed buys extra assets through the subscription change when asked (#274) |
| S-4 | An enrolment with a key off the P-256 curve ended in a 500 on Windows (CNG throws another exception) | Both exceptions mean "not a key" (#275) |
| D-1 | Found by the demo rehearsal: after a material correction, the review panel tried to verify the corrected version's signature. A correction made in the office has no envelope (all-zero ID), so the portal asked for `/audit/envelopes/00000000-…/verification`, got a 404 and showed the error dialog over the panel | The portal verifies nothing for a version without an envelope (`noEnvelope`, `SignatureVerification`); unit test added (#277) |

## 5. Known issues and open items

- **P2.12b (owner first):** staging (P1.17), device lab tablets (cold start, PIN unlock, QR scan, the 72 h run on a
  real low-end tablet), the pilot site for the shadow run, the operator and controller usability tests.
- **Residual risk R-16:** the public check's rate limit is per API instance and document tokens cannot be revoked yet:
  both come with the deployment.

## 6. Decision (to be completed at the gate)

| | |
|---|---|
| Decision | _Go / Go with conditions / No-go_ |
| Conditions | |
| Decided by, on | |

---
title: "Phase 1 Exit Review (M1 Walking Skeleton): Draft"
area: phases
tags: [phase-1, exit-review, m1, gate, evidence, hardening, demo]
source: "P1.18a slice 7 (hardening plan); exit gate in plan §8.9"
status: draft
last_updated: 2026-11-09
---

# Phase 1 exit review (M1): draft

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 1 plan](plan.md) · [Sub-phases](sub-phases.md) · [Tracker](tracker.md) · [Hardening plan](hardening-plan.md)

**Status: draft, no decision yet.** This draft was written at the end of P1.18a (local stack and CI). The gate cannot
be decided now: three of its five boxes need a hosting provider (staging, restore drill, pilot data in staging), and
that choice is still open ([D49](../../00-llm/llm_hands_off.md), [D56](../../00-llm/llm_hands_off.md)). The final
version is written in P1.18b, after P1.17, and needs the owner's acceptance. Until then, each section below says what
is proven and what is still missing.

**Recommendation for now: _Go with conditions_ for starting Phase 2 work (already agreed in D56), _No-go_ for
declaring M1.** The product works end to end on the local stack and in CI, and P1.18a found and fixed 19 defects ([§4](#4-what-hardening-found)).
M1 is a promise about a hosted, restorable system with a real customer's data, and none of those exist yet.

## 1. Exit gate checklist ([plan §8.9](plan.md#89-exit-gate-m1--walking-skeleton))

| Box | Status | Evidence / what is missing |
|---|---|---|
| All Must stories done; isolation suite green; ledger verifier green | 🟡 | 41 of the 42 Must stories are built and tested in CI ([§2](#2-must-stories)). The isolation suite (`tenant-isolation`, a required check) and the ledger verifier (`TC-P1-AUD-*`) are green on every PR. Missing: US-P1-PLT-04 (CD to dev and staging, P1.17); also still to do: the real-tablet run of the field app (P1.15, needs a tablet) and delivering US-P1-OPS-02's alerts to a real receiver (needs the hosting provider) |
| Staging deploy from merge in < 30 min; rollback tested | ⬜ | Needs P1.17 (hosting provider, WP-P0-14) |
| Restore drill executed with RTO/RPO documented | ⬜ | Needs the managed PostgreSQL of P1.17; the local resilience scenarios (H-R1–R5) passed, but they are not a restore drill |
| Pilot customer's master data loaded in staging | ⬜ | Needs staging and the pilot's files (owner input). The import path is proven with test workbooks (US-P1-ORG-03, `TC-P1-ORG-*`) |
| No Sev-1/Sev-2 open | ✅ (today) | No open defect of severity 1 or 2 ([triage rules](../../05-quality/defect-severity-and-triage.md)). Every P1.18a finding was fixed in its own PR ([§4](#4-what-hardening-found)). Check again at the real gate |

## 2. Must stories

All Phase 1 stories are **Must** ([plan](plan.md)). The status comes from the [tracker](tracker.md); the evidence for
each sub-phase (PRs, CI runs, measurements) is listed there.

| Area | Stories | Status |
|---|---|---|
| Platform | PLT-01 monorepo and template, PLT-02 local environment, PLT-03 CI gates, PLT-05 feature flags | ✅ |
| Platform | PLT-04 CD to dev and staging with IaC | ⏳ P1.17, waiting for the hosting provider |
| Operations | OPS-01 tracing and metrics, OPS-03 operator console | ✅ |
| Operations | OPS-02 alerting and error tracking | ✅ as code (5 alert rules with promtool tests, runbooks, frontend error tracking); delivery to a pager or e-mail is set up with the hosting provider |
| Identity | IAM-01 OIDC sign-in, IAM-02 MFA for privileged roles, IAM-03 invitations, IAM-04 default roles, IAM-05 custom roles, IAM-06 scopes, IAM-07 field-level masking | ✅ |
| Tenancy | TEN-01 provisioning, TEN-02 RLS framework, TEN-03 isolation suite, TEN-04 settings | ✅ |
| Billing | BIL-01 plan catalogue, BIL-02 subscriptions, BIL-03 feature gating, BIL-04 usage and soft limits, BIL-05 state enforcement | ✅ |
| Organization and assets | ORG-01 sites, ORG-02 shifts, ORG-03 Excel import, AST-01–04 asset types, registry, meters, materials, UNI-01 units engine | ✅ |
| Audit | AUD-01 hash-chained ledger, AUD-02 WORM anchoring, AUD-03 verifier, AUD-04 audit viewer | ✅ |
| Field app | OFF-01 offline shell, OFF-02 enrolment, OFF-03 offline reference data, OFF-04 shared tablet, OFF-05 design system and outdoor mode, OFF-06 FR/EN | ✅ in code, E2E in a desktop browser; ⏳ the run on a real tablet (owner item) |

## 3. Demo script rehearsal ([plan §8.8](plan.md#88-demo-script))

A rehearsal on the local stack, step by step: what an automated test already proves, and what a person still has to
show. The demo itself, recorded, is part of P1.18b (staging).

| Step | Proven by | Still to rehearse by hand |
|---|---|---|
| 1. The operator provisions "Carrière Pilote SA" on a Professional trial | E2E `operator-console.spec.ts` (an operator with a second factor creates a company and changes its plan); `TC-P1-TEN-*`, `TC-P1-BIL-*` | Nothing beyond the company name |
| 2. The tenant admin signs in (FR), enrols MFA, uploads the logo, imports 60 machines from Excel (errors shown, fixed, committed) | E2E `sign-in.spec.ts` (TC-P1-IAM-001 French and branded, TC-P1-IAM-002 MFA required); portal tests of the company form and the import wizard (errors on their cells, commit refused until fixed); `TC-P1-ORG-*` import API tests with real workbooks | The 60-machine workbook with errors in it is not in the repository yet: prepare it from the template before the demo |
| 3. A custom role "Chef de poste" with cost fields masked, and the API answer masked | Portal role editor tests; masking suite TC-P1-IAM-005 (every sensitive field of the OpenAPI contract); TC-P1-IAM-009 allow/deny matrix | Showing the masked JSON live: use the browser's network panel or `curl` with the role's token |
| 4. Enrolment QR, the tablet enrols, airplane mode, PIN unlock, offline asset search | E2E `field-enrollment.spec.ts` (TC-P1-OFF-001/002/003/008: enrolment, unlock, site data kept offline, re-enrolment); race tests H-C2 | On a real tablet with its camera (owner item); the E2E runs in desktop Chromium |
| 5. The audit viewer shows every action; integrity check OK; tamper demo on a throwaway database detected | E2E `operator-console.spec.ts` (TC-P1-OPS-004: the company sees the session and runs the integrity check); TC-P1-AUD-001…006 (tampering found at the exact sequence; WORM anchoring) | The tamper demo needs a throwaway database: never on the demo company's stack (anchors are under compliance retention, so a tamper alarm stays forever, as found in H-C5) |
| 6. Plan and usage page; switch to Starter, the Maintenance menu shows the upgrade card | `TC-P1-BIL-*` (feature gating, usage, limits); portal `PlanPage` and `FeatureGate` tests (the upgrade card, TC-P1-BIL-003) | No Phase 1 screen sits behind `FeatureGate` yet (Maintenance comes in a later phase): pick a gated feature for the demo or change step 6 |

**Gaps to close before the recorded demo:** a demo workbook with deliberate errors (step 2); a throwaway database
for the tamper demo (step 5); the upgrade card in step 6 points at a module that does not exist yet; the real tablet
(step 4). An automated end-to-end run of the whole script (one Playwright file) would turn this table into a
regression test. It is listed as a follow-up and is not needed for the gate.

## 4. What hardening found

P1.18a ([hardening plan](hardening-plan.md)) tried to break the system on the local stack and in CI. Each finding was
reproduced by a test first, then fixed, and each fix was mutation-checked.

| Finding | What was wrong | Fixed by |
|---|---|---|
| T-12 | No API rate limiting, though the threat model promised it | Rate limits per address, tablet, person, anonymous address and company (#124) |
| Request limits | Unbounded bodies; a malformed body was a 500 | 1 MiB default, per-endpoint limits, 413/400 (#125) |
| L-1 | One connection pool per DbContext type: the database ran out of connections under load | One pool per process (#127, D50) |
| H-I6 | The reset-password flow asked no second factor and removed the OTP: an inbox alone could take over a Tenant Admin | Own reset flow that keeps and asks the second factor (D51) |
| H-I1 | The API accepted a token 5 minutes past its expiry, with any algorithm the key allowed | 30 s and RS256 only |
| W-1 | No security headers on API answers | Headers on every answer; page policy with an E2E guard (D52) |
| W-3 | The site map was broken in every production build (MapLibre's worker file never shipped) | Worker shipped and loaded explicitly; `map.spec.ts` |
| H-C5 | Two Workers anchoring at once wrote two versions of one anchor: a permanent false tamper alarm | Advisory lock around the whole anchoring run (#130, D53) |
| R-1, R-2, R-3 | Readiness hung 60 s with the database down; a storage outage was a slow 500; a saved photo change was reported as failed | Fast readiness, 503 with `Retry-After`, correct reporting |
| C-1 | A later save silently overwrote an earlier one: no `If-Match`, though the API standard promised it | Record versions on sites, materials, assets, company settings, subscriptions, roles and user roles (D55) |
| C-2 | Portal forms were remounted on refresh: what the person typed was replaced by someone else's change | `useRecordDraft` and the conflict notice |
| C-3 | A refused logo save had already deleted the logo | One transaction for the logo and the company |
| C-4 | The subscription's row-version conflict was never caught: a 500 for operators, a failed lifecycle run | Conflict reported, 409 |
| C-5 | Crossing uploads of a tablet deadlocked: the tablet got 503 | Per-tablet lock on the inbox insert |
| C-6 | Shifts and flag overrides set at once answered 500 (unique violations, deadlocks) | Conflicts reported as 409; per-company lock on shift writes |
| C-7 | A retried upload read the inbox in two steps: with a crossing upload in between, its own commands came back `sync.sequence_reused` instead of `duplicate` | Both reads in one snapshot (#146) |
| H-W3 | 5 threats claimed for Phase 1 cited no test | Traceability check in CI; the threat model cites its tests |

## 5. Metrics

| Measure | Value | Source |
|---|---|---|
| Backend test methods (xUnit `Fact`/`Theory`) | about 790 | count on 2026-11-09 |
| Frontend unit and component tests (Vitest) | about 320 | count on 2026-11-09 |
| Browser end-to-end tests (Playwright, real stack) | 12 | `src/frontend/e2e/tests` |
| CI tool tests (Python) | 48 | `tools/`, `tests/` |
| 500 tablets pulling reference data (burst / shift) | 0 errors; burst p95 751 ms; shift 15,579 pulls, p95 27 ms | [load testing](../../05-quality/load-testing.md) |
| TC-P1-TEN-005: 10,000 mixed requests from two companies | 0 leaks; p95 33 ms (laptop), 109 ms (nightly) | k6 `tenants.js` |
| Spike 0 → 300 users in 10 s | 0 server errors | k6 `tenants.js` |
| ZAP baseline | no high findings; one accepted medium (`style-src 'unsafe-inline'`, with its reason) | [security testing](../../05-quality/security-testing.md) |
| Threats claimed for Phase 1 and traced to tests | 12 of 12 | `tools/ci/threat_traceability.py` |

## 6. Known issues and open items

- **Waiting for the hosting provider:** P1.17 (environments and CD), P1.18b (staging load and ZAP, failover, restore
  drill, UAT, the recorded demo), the cloud KMS adapter, the `verify.` domain, the real e-mail provider, and the
  staging part of P2.12b.
- **Waiting for the owner:** a real tablet for the P1.15 run; the pilot customer's master data; demo attendance;
  native reviewers for the Arabic, Malinké and N'Ko drafts ([translation review](../../05-quality/translation-review.md));
  the Renovate app install.
- **`If-Match` is optional:** a save without it applies to the current version, so that clients could move over
  gradually. Every portal form now sends it. Making it required (**428**) is a small follow-up.
- **Tablets and support sessions** still report a concurrent change as `identity.concurrent_change` (not
  `request.version_conflict`). It is harmless and documented in the API standards.
- **Local-machine traps** (not product defects): demo company B's ledger shows the H-C5 tamper alarm on the owner's
  machine until a fresh stack. This was cleared on 2026-11-04 and comes back only if anchors are written twice again.
  A privileged demo user enrols the second factor again after a reset.

## 7. Lessons

1. **A race test over HTTP proves little on its own.** Several mutations survived concurrent HTTP requests and were
   caught only when the interleaving was forced through a port (C-3, the provisioning race), or when the test class
   was run several times (C-6).
2. **"Never a 500" is a test of its own.** Unique violations and deadlocks under concurrency surfaced as 500 or 503 in
   five places (C-4, C-5, C-6, the logo insert, H-C5). Each store now reports them as conflicts.
3. **Promises in the docs need a gate.** The API standard promised `If-Match` and the threat model promised rate
   limits; neither existed. The threat traceability check now fails the build when a claim loses its test.
4. **The dev server hides production defects.** The broken map (W-3) only showed in a production build under the
   page policy.
5. **On a 14 GB machine, memory is part of the workflow.** Keycloak capped at 768 MB, no MSBuild node reuse, targeted
   test runs, and every server stopped after use.

## 8. Decision (to be completed at the gate)

| | |
|---|---|
| Decision | Pending: Go / Go with conditions / No-go |
| Conditions | |
| Decided by, date | Owner, after P1.18b |

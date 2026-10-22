---
title: "Delivery Framework (Cadence, DoR, DoD)"
area: quality
tags: [scrum, definition-of-ready, definition-of-done, test-levels, releases]
source: "PHASES_DETAILED_PLAN.md §4"
status: baseline
last_updated: 2026-10-21
---

# Delivery Framework (Cadence, DoR, DoD)

> [Docs index](../README.md) › [05-quality](README.md)

## 4.1 Cadence

| Ceremony | Frequency | Output |
|---|---|---|
| Sprint | 2 weeks | Potentially shippable increment on staging |
| Sprint planning | Start of sprint | Sprint goal + committed stories (all meet DoR) |
| Daily stand-up | Daily, 15 min | Blockers surfaced |
| Backlog refinement | Weekly, 1 h | Stories ready for next 2 sprints |
| Sprint review / demo | End of sprint | Demo on staging, feedback logged (pilot customer invited from P2) |
| Retrospective | End of sprint | 1–3 improvement actions |
| Architecture review | Weekly, 30 min | ADRs proposed/approved |
| Phase exit review | End of each phase | Gate decision: Go / Go with conditions / No-go |
| Security review | Each phase + any trust-layer change | Threat model update, findings tracked |

## 4.2 Definition of Ready (DoR), required before a story enters a sprint

- [ ] Story written in template ([§1.3](../04-phases/conventions.md#13-story-template)) with persona and business outcome.
- [ ] Acceptance criteria are testable (Given/When/Then), including error and offline cases.
- [ ] UX designs/wireframes attached (or "no UI" stated).
- [ ] API contract sketched (endpoints, payloads) when the story touches the backend.
- [ ] Permissions identified (which roles, which scope, which fields masked).
- [ ] Audit events identified (what goes in the ledger).
- [ ] i18n keys identified (FR/EN copy reviewed by PO).
- [ ] Dependencies resolved or scheduled.
- [ ] Size ≤ M (L stories split).
- [ ] Test cases drafted by QA (IDs reserved).

## 4.3 Definition of Done (DoD), required for every story

- [ ] All AC pass, verified by QA on the `dev` environment.
- [ ] Cross-cutting AC ([§5](cross-cutting-acceptance-criteria.md)) satisfied.
- [ ] Unit tests written; domain/math coverage ≥ 80 % for touched modules; property tests for formulas.
- [ ] Integration tests for persistence and API (Testcontainers).
- [ ] E2E test added or updated for user-facing critical journeys.
- [ ] Tenant-isolation test auto-generated for any new endpoint (passes).
- [ ] OpenAPI updated, generated client regenerated, no breaking change (or versioned).
- [ ] Migrations are reversible or follow expand/contract; reviewed as SQL.
- [ ] Telemetry: traces/metrics/logs added for new flows; dashboards updated if needed.
- [ ] Feature flag / entitlement wiring if the feature is plan-gated.
- [ ] Docs updated (user help article stub FR/EN, ADR if decision taken, runbook if operational).
- [ ] Code reviewed (2 approvals for `Trust`, `Tenancy`, `IAM`, `Billing`, `Sync` modules).
- [ ] No new high/critical findings from SAST, dependency or container scans.
- [ ] Merged to `main`, deployed to `dev`, demoable.

## 4.4 Definition of Done, phase level

- All Must stories done; Should stories done or descoped in writing.
- Phase test report signed by QA lead (test execution summary, pass rate, open defects by severity).
- **Zero open Sev-1/Sev-2 defects**; Sev-3 have a plan.
- Performance and security tests of the phase executed and within thresholds.
- Release notes (FR/EN), user help articles, admin documentation updated.
- Demo accepted by PO; from P2 onward also by pilot customer representative.
- Retrospective of the phase + re-plan of the next phase.

## 4.5 Estimation & capacity assumptions

- Team: 1 tech lead/architect, 2 senior backend, 1–2 frontend, 1 QA, 0.5–1 DevOps, 1 PO, 0.5 UX; data engineer from P4.
- Velocity is calibrated after 2 sprints. Until then, plan with **≈ 60 %** of the theoretical capacity for feature work. The remaining 40 % covers bugs, reviews, support for the pilot, tech debt and meetings.
- Each phase reserves a **hardening week** at its end (bug fixing, performance, documentation, demo prep).

## 4.6 Test strategy (summary of levels used in every phase test plan)

| Level | Owner | Tool | Gate |
|---|---|---|---|
| Unit | Dev | xUnit / Vitest | PR |
| Property-based | Dev | FsCheck / fast-check | PR |
| Mutation (math, trust) | Dev | Stryker.NET | Nightly, threshold ≥ 70 % |
| Integration | Dev | Testcontainers (Postgres+PostGIS+Timescale, Keycloak, SeaweedFS S3) | PR |
| Architecture | Dev | `tests/Karry.ArchitectureTests` (project graph; ArchUnitNET type rules from P1.5) + module template check | PR |
| Contract | Dev | OpenAPI diff | PR |
| Tenant isolation | QA/Dev | Generated suite | PR + nightly |
| E2E web | QA | Playwright | Main branch |
| E2E offline | QA | Playwright with network emulation + service worker | Main branch, nightly |
| Device lab | QA | Real tablets (see [Appendix F – Test Environments, Data & Device Matrix](test-environments-and-devices.md)) | Per sprint for field features |
| Performance | QA/DevOps | k6 | Per phase + before release |
| Security | Security champion | ZAP, dependency scanning, manual review, external pen test (P10) | Per phase |
| Accessibility | QA/UX | axe-core + manual | Per release |
| Localization | PO/QA | FR/EN review, pseudo-localization build | Per release |
| UAT | Pilot users + PO | Scripted scenarios | Per phase from P2 |
| AI evaluation | Dev/QA | Golden sets | Every prompt/model change |

## 4.7 Branching, environments & releases

- Trunk-based, short-lived feature branches (≤ 2 days), feature flags for incomplete work.
- `main` → auto-deploy to `dev` → nightly to `staging` (tagged RC) → manual promotion to `prod`.
- Production releases: weekly during pilot, then continuous with progressive rollout (canary 10 % → 100 %).
- PWA compatibility: server supports the current and the **two previous** client versions, because offline devices update late.

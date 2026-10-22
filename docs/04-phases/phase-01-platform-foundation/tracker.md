---
title: "Phase 1 Tracker: Sub-Phases, Evidence & Exit Gate"
area: phases
tags: [phase-1, tracker, status, sub-phases, exit-gate]
source: "Phase 1 execution"
status: draft
last_updated: 2026-10-22
---

# Phase 1 Tracker: Sub-Phases, Evidence & Exit Gate

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 1 plan](plan.md) · [Sub-phases](sub-phases.md)

Live status of [Phase 1](plan.md), executed in the order of the [sub-phase plan](sub-phases.md). Update this file when a sub-phase starts, when evidence is produced (PRs, CI run IDs, measurements), and when it finishes.

Legend: ✅ done · 🟡 in progress · ⏳ waiting on owner or external input · ⬜ not started

## 1. Sub-phases

| # | Sub-phase | Status | Evidence (PRs, CI runs, measurements) / next action |
|---|---|---|---|
| P1.0 | Kick-off & prerequisites | ✅ | Toolchain verified (.NET SDK 10.0.402; Node 24.22.1; pnpm; gh; actionlint 1.7.12). Versions recorded ([tech stack §7.1.1](../../02-architecture/technology-stack.md#711-verified-versions-phase-1-start-2026-10-22)). ADR-0017 … 0020 written; **ADRs 0001–0020 accepted** (owner, 2026-10-22). **Merge rule** decided (sub-phases §2.3). ⚠ Docker access for the agent still pending (owner re-login): Docker-based checks run in GitHub Actions until then |
| P1.1 | Monorepo, module template, local environment | ✅ | PRs [#1](https://github.com/diya-thabet/Karry-Platform/pull/1) backend skeleton, [#2](https://github.com/diya-thabet/Karry-Platform/pull/2) module template + architecture rules, [#3](https://github.com/diya-thabet/Karry-Platform/pull/3) frontend workspace, [#4](https://github.com/diya-thabet/Karry-Platform/pull/4) local stack + README. `main` CI run 39841100580 green (backend 29 tests + template check, frontend 2 tests + lint/typecheck/build, local-stack 13/13 checks). Tests: TC-P1-PLT-001, 003, 004, 012, 013, 014, 015. Mutation checks: readiness ignoring DB (2 failures), forbidden host reference (caught), floating promise (lint error). PLT-02 AC2 (seed tenants/users) moves to P1.5/P1.8; AC3 (onboarding < 60 min) measured at P1.18 |
| P1.2 | CI pipeline & repository governance | ⬜ | |
| P1.3 | Shared kernel & building blocks | ⬜ | |
| P1.4 | Observability baseline | ⬜ | |
| P1.5 | Tenancy & isolation | ⬜ | |
| P1.6 | Audit ledger core | ⬜ | |
| P1.7 | Frontend foundations | ⬜ | |
| P1.8 | Identity & authentication | ⬜ | |
| P1.9 | Authorization: roles, scopes, masking | ⬜ | |
| P1.10 | Plans, entitlements, subscriptions & feature flags | ⬜ | |
| P1.11 | Organization, sites, shifts & tenant settings | ⬜ | |
| P1.12 | Materials & units engine | ⬜ | |
| P1.13 | Assets & meters | ⬜ | |
| P1.14 | Excel import | ⬜ | |
| P1.15 | Field app: install, enrollment, offline data, shared tablet | ⬜ | |
| P1.16 | Platform operator console & audit viewer | ⬜ | |
| P1.17 | Environments & continuous delivery | ⏳ | Blocked on hosting provider & region (WP-P0-14) |
| P1.18 | Hardening, non-functional tests, demo & exit gate M1 | ⬜ | |

## 2. M1 exit gate

| Criterion (from [plan §8.9](plan.md#89-exit-gate-m1--walking-skeleton)) | Status |
|---|---|
| All Must stories done; isolation suite green; ledger verifier green | ⬜ |
| Staging deploy from merge in < 30 min; rollback tested | ⬜ |
| Restore drill executed with RTO/RPO documented | ⬜ |
| Pilot customer's master data loaded in staging | ⬜ |
| No Sev-1/Sev-2 open | ⬜ |

## 3. Findings that changed the plan

1. **MinIO open source is archived** (April 2026, no community images): local/on-prem S3 store is now **SeaweedFS**, with an Object Lock acceptance test in P1.1 ([ADR-0017](../../adr/0017-local-stack-and-object-storage.md)).
2. **typescript-eslint does not support TypeScript 7**: product code uses TypeScript 6.0.3 until it does ([ADR-0020](../../adr/0020-frontend-toolchain-versions.md)).
3. **`exceljs` has had no release since 2024**: Excel parsing moves server-side (ClosedXML), which also fits the server-side validation of US-P1-ORG-03.
4. **Node 24 is the current LTS** (not 22): pinned for the product repository.
5. **SeaweedFS needs explicit volume sizing** (`-master.volumeSizeLimitMB=256 -volume.max=64`): with defaults it found no writable volume on the CI runner. With that, its **compliance-mode Object Lock holds** (delete and retention shortening refused): ADR-0017 acceptance test passed.
6. **The template test caught a real defect on its first run** (generated tests violated our analyzer rules); module test projects now share the test code-style exemptions.
7. **Architecture rules are enforced on the project reference graph** (not assemblies) for now; type-level ArchUnitNET rules are added in P1.5 with the first real module.

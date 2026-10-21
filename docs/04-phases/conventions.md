---
title: "Phase Plan Conventions (IDs & Story Template)"
area: phases
tags: [conventions, ids, story-template]
source: "PHASES_DETAILED_PLAN.md §1"
status: baseline
last_updated: 2026-10-21
---

# Phase Plan Conventions (IDs & Story Template)

> [Docs index](../README.md) › [04-phases](README.md)

## 1.1 Who uses what

| Reader | Read first |
|---|---|
| Founder / CEO | [§2](../01-product/business-model-and-pricing.md) (business model), [§3](../02-architecture/modular-monolith-decision.md) (architecture decision), [§6](phases-overview.md) (phase → product packaging), [§22](../01-product/kpis.md) (KPIs) |
| Product Owner | [§4](../05-quality/delivery-framework.md), [§5](../05-quality/cross-cutting-acceptance-criteria.md), then each phase's *Epics & User Stories* |
| Engineers | [§3](../02-architecture/modular-monolith-decision.md), [§4](../05-quality/delivery-framework.md), [§5](../05-quality/cross-cutting-acceptance-criteria.md), then each phase's *Technical Scope* and *Test Plan* |
| QA | [§4.6](../05-quality/delivery-framework.md#46-test-strategy-summary-of-levels-used-in-every-phase-test-plan), [§5](../05-quality/cross-cutting-acceptance-criteria.md), each phase's *Test Plan*, Appendices E & F |
| Sales / Customer Success | [§2](../01-product/business-model-and-pricing.md), [§6](phases-overview.md), [§19](../01-product/go-to-market.md), [§20](../01-product/customer-onboarding-playbook.md) |

## 1.2 Identifier conventions

| Item | Format | Example |
|---|---|---|
| Epic | `EP-P<phase>-<nn>` | `EP-P2-03` |
| User story | `US-P<phase>-<AREA>-<nn>` | `US-P2-FO-04` (Phase 2, Field Ops, story 4) |
| Acceptance criterion | `AC<n>` inside a story | `US-P2-FO-04 / AC3` |
| Test case | `TC-P<phase>-<AREA>-<nnn>` | `TC-P2-SYNC-012` |
| Non-functional requirement | `NFR-<AREA>-<nn>` | `NFR-OFF-01` |
| Detection rule | `RULE-<AREA>-<nn>` | `RULE-FUEL-03` |

Area codes: `PLT` platform, `TEN` tenancy, `IAM` identity & access, `BIL` billing, `ORG` organization, `AST` assets, `UNI` units, `AUD` audit ledger, `OFF` offline/sync, `FO` field ops, `VER` verification, `SIG` signatures, `WB` weighbridge, `SAL` sales, `FUEL` fuel, `INT` integrity dashboard, `NOT` notifications, `MNT` maintenance, `INV` inventory, `PLN` plant, `PRD` production, `STK` stockpiles, `EDG` edge agent, `FIN` finance, `WX` weather, `ANA` analytics, `AI` copilot, `API` public API, `TEL` telematics, `GRW` growth/self-service, `OPS` platform operations.

## 1.3 Story template

Every user story in the backlog follows this exact template:

```
ID:            US-P2-FO-01
Title:         Start a shift on an asset
Persona:       Field Operator
Story:         As a <persona>, I want <capability>, so that <business outcome>.
Priority:      Must | Should | Could   (MoSCoW, within the phase)
Size:          S (≤2 days) | M (3–5 days) | L (6–10 days)   — L stories must be split before sprint entry
Dependencies:  other story IDs
Acceptance:    Given / When / Then criteria (AC1…ACn)
Test refs:     TC-… identifiers
Notes:         UX / data / edge cases
```

In this document the stories are written in a compact table and list form for readability. The backlog tool (Jira / Linear / GitHub Projects) holds the full template.

## 1.4 What "done" means at each level

| Level | Done means |
|---|---|
| Story | Meets its AC + all cross-cutting AC ([§5](../05-quality/cross-cutting-acceptance-criteria.md)) + Definition of Done ([§4.4](../05-quality/delivery-framework.md#44-definition-of-done-phase-level)) |
| Epic | All Must stories done, all Should stories done or explicitly descoped by PO |
| Phase | Exit gate passed (each phase lists it), demo accepted by PO + pilot customer (from P2), test report signed |
| Release | Release checklist ([§21](../06-operations/release-management.md)) completed, deployed to production, release notes published FR/EN |

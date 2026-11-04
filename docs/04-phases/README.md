---
title: "Phases & Delivery Plan"
area: phases
tags: [phases, roadmap, user-stories, acceptance-criteria, test-plans]
source: "created 2026-10-21"
status: baseline
last_updated: 2026-10-21
---

# 04-phases — Roadmap & Phase Plans

> [Docs index](../README.md)

## Cross-phase files

| File | Purpose |
|---|---|
| [phases-overview.md](phases-overview.md) | Timeline, milestones, which plan becomes sellable after which phase, dependency graph |
| [roadmap-summary.md](roadmap-summary.md) | Short roadmap with Gantt and milestone definitions (phase plans win when they differ) |
| [conventions.md](conventions.md) | ID conventions (`EP`, `US`, `TC`, `NFR`, `RULE`), story template, levels of "done" |
| [story-index.md](story-index.md) | All 225 user stories with phase, priority, size, linked to their phase plan |
| [requirements-traceability.md](requirements-traceability.md) | Original requirements → module → section → phase |

## One folder per phase

| Phase | Folder | Commercial outcome |
|---|---|---|
| P0 Discovery & architecture baseline | [phase-00-discovery](phase-00-discovery/plan.md) | Validated pricing, pilot LOI |
| P1 Platform foundation & SaaS core | [phase-01-platform-foundation](phase-01-platform-foundation/plan.md) | Plans & entitlements live |
| P2 Field ops, verification & signatures | [phase-02-field-operations](phase-02-field-operations/plan.md) ([sub-phases](phase-02-field-operations/sub-phases.md), [tracker](phase-02-field-operations/tracker.md)) | Core differentiator demoable |
| P3 Weighbridge, fuel, integrity & pilot | [phase-03-weighbridge-fuel-pilot](phase-03-weighbridge-fuel-pilot/plan.md) | **Starter sellable** |
| P4 Maintenance & reliability | [phase-04-maintenance](phase-04-maintenance/plan.md) | Upsell to Professional |
| P5 Inventory & warehouses | [phase-05-inventory-warehouses](phase-05-inventory-warehouses/plan.md) | **Professional sellable** |
| P6 Plant, production, stockpiles, edge | [phase-06-plant-production-edge](phase-06-plant-production-edge/plan.md) | Commercial v1 |
| P7 Finance, weather & analytics | [phase-07-finance-weather-analytics](phase-07-finance-weather-analytics/plan.md) | **Business sellable** |
| P8 AI copilot | [phase-08-ai-copilot](phase-08-ai-copilot/plan.md) | AI add-on |
| P9 Integrations, telematics & API | [phase-09-integrations-api](phase-09-integrations-api/plan.md) | **Enterprise sellable** |
| P10 SaaS GA & self-service | [phase-10-saas-ga](phase-10-saas-ga/plan.md) | General availability |
| P11+ Evolution backlog | [phase-11-evolution-backlog](phase-11-evolution-backlog/plan.md) | Continuous growth |

### What goes in a phase folder

| File | When | Content |
|---|---|---|
| `tracker.md` | During the phase | Live status of work packages/stories, evidence links, test results, exit-gate checklist (Phase 0: [tracker](phase-00-discovery/tracker.md)) |
| `plan.md` | Baseline (exists) | Objectives, scope, epics, user stories + AC, technical scope, test plan, deliverables, demo, exit gate, risks |
| `sub-phases.md` | Before the phase starts | Execution order: sub-phases with stories, dependencies, owner inputs, tests and done criteria; the owner starts them one by one (Phase 1: [sub-phases](phase-01-platform-foundation/sub-phases.md); P1.18a [hardening plan](phase-01-platform-foundation/hardening-plan.md)) |
| `*-explained.md` | Optional | Plain-English explanation for non-technical readers (Phase 0: [spikes explained](phase-00-discovery/spikes-explained.md)) |
| `sprint-NN.md` | Each sprint | Sprint goal, committed stories, outcome, carry-over |
| `test-report.md` | End of phase | Execution summary per `TC-…`, pass rate, defects by severity, NFR results |
| `exit-review.md` | Phase gate | Exit criteria checklist with evidence, Go / Go-with-conditions / No-go decision |
| `retrospective.md` | End of phase | What worked, what to change, actions |

---
title: "Phase 4 — Maintenance & Reliability"
area: phases
tags: [phase-4, maintenance, rul, work-orders]
source: "PHASES_DETAILED_PLAN.md §11"
status: baseline
last_updated: 2026-10-21
---

# Phase 4 — Maintenance & Reliability

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 6 weeks (W20–W25) · **Team:** core team + data engineer joins

## 11.1 Objectives & business value

Prevent **unplanned downtime**. In remote quarries a broken jaw liner can stop production for weeks, which can cost far more than a year of Karry subscription. This phase makes the Professional plan worth its price.

## 11.2 Scope

**In:** component & wear-part catalogue, component installation on assets, ratings (hours/km/days/tonnage), lead times, hybrid RUL engine with meter fallback estimation, PM plans, work orders (corrective & preventive), mechanic mobile app (offline, signed), procurement alerts (stock-aware stub until P5), maintenance grouping v1, maintenance dashboards & wear trajectories, downtime tracking, in-app upgrade prompts.
**Out:** spare-parts stock movements (P5), Weibull models (P11), telematics fault codes (P9).

## 11.3 Epics

| Epic | Title |
|---|---|
| EP-P4-01 | Component catalogue & installed base |
| EP-P4-02 | RUL engine & fallback estimation |
| EP-P4-03 | Preventive maintenance plans |
| EP-P4-04 | Work orders & mechanic app |
| EP-P4-05 | Downtime tracking |
| EP-P4-06 | Procurement alerts |
| EP-P4-07 | Maintenance grouping |
| EP-P4-08 | Maintenance dashboards |
| EP-P4-09 | Upsell prompts |

## 11.4 User stories & acceptance criteria

### EP-P4-01 Component catalogue & installed base

**US-P4-MNT-01 — Component types catalogue** · *Persona: Maintenance Planner* · Must · M
- AC1: Component type: name (FR/EN), category (wear part, fluid, filter, tyre/track, structural, electrical), applicable asset types, rating metric(s) (HOURS, KM, DAYS, TONNES) with default rated life, abrasion severity mapping, criticality (A/B/C), default supplier and lead time, part numbers (OEM & aftermarket).
- AC2: Seeded library for common quarry equipment (jaw liners, cone mantle/concave, blow bars, screen meshes, conveyor belts/idlers, bucket teeth, track shoes, engine oil, hydraulic oil, filters, tyres).
- AC3: Import from Excel.

**US-P4-MNT-02 — Install a component on an asset** · Must · M
- AC1: Record installation: asset, position (e.g., "left jaw plate", "deck 2 mesh"), component type, serial/lot (optional), install date, meter at install, tonnage at install (if node in plant later), rated life override, cost.
- AC2: Removal records: date, meter, reason (worn, failure, preventive), condition photo, measured wear (optional mm).
- AC3: Installed-base view per asset: current components with RUL; history of past components with achieved life.

**US-P4-MNT-03 — Achieved life tracking** · Must · S
- AC1: On removal, achieved life in each metric is computed and stored; comparison to rated life (%) shown.
- AC2: This data feeds future reliability models (P11).

### EP-P4-02 RUL engine

**US-P4-MNT-04 — RUL computation** · *Persona: Maintenance Planner* · Must · L→split
As a planner, I want to see how many days each component has left, so that I can plan stops and order parts on time.
- AC1: For each installed component compute `RUL_usage`, `RUL_tonnage` (if rated in tonnes and data exists, otherwise skipped), `RUL_calendar` (if rated in days), and `RUL_days = min(...)` per [Computational Engines (Corrected Mathematics) §10.5](../../03-domain/computational-engines.md#105-hybrid-maintenance--rul-engine).
- AC2: `ū_daily` = trimmed mean (10 %) of the last 30 operating days of verified usage; configurable window; requires ≥ 7 days of data, otherwise uses asset-type default usage.
- AC3: Recomputed on every verified shift entry affecting the asset (event-driven) and nightly for all.
- AC4: Each RUL value shows its inputs ("show calculation"): rating, accumulated, daily rate, abrasion factor, metric chosen, formula version.
- AC5: Values tagged ESTIMATED when based on fallback data.

**US-P4-MNT-05 — Meter fallback estimation** · Must · M
- AC1: When a meter is FAILED (P2), accumulated usage = last valid + ū_daily(30d before failure) × operating days since failure (using shift entries "operating hours estimate" where provided, otherwise calendar operating days from site calendar).
- AC2: When the meter is repaired/replaced, the estimate is reconciled with the real reading; the difference is shown and accumulated usage corrected going forward (history kept).
- AC3: Alerts continue without interruption during the failure (TC proves).

**US-P4-MNT-06 — Abrasion severity configuration** · Should · S
- AC1: Per site/material, configure abrasion class (low/medium/high/very high, or Bond Ai value mapped to δ) used by components flagged abrasion-sensitive.
- AC2: Changes versioned; RUL recalculated.

### EP-P4-03 Preventive maintenance plans

**US-P4-MNT-07 — PM plans** · Must · M
- AC1: Plan per asset type or asset: tasks (e.g., 250 h service, 500 h service, weekly greasing) with interval in hours/km/days (whichever first), task checklist, parts kit, estimated duration.
- AC2: Next due computed from last completion and current usage; due-soon window configurable.
- AC3: Auto-generate PM work orders X days/hours before due.

### EP-P4-04 Work orders & mechanic app

**US-P4-MNT-08 — Work order lifecycle** · *Persona: Workshop Chief* · Must · M
- AC1: Types: preventive, corrective, inspection, component replacement. States: Requested → Approved → Scheduled → In progress → Completed → Verified → Closed (cancel allowed before In progress).
- AC2: Fields: asset, priority, description, requested by, assigned mechanics, planned start/end, downtime required (Y/N), tasks checklist, parts planned/used (free text until P5), labour hours, external costs, photos.
- AC3: Breakdown request can be created from the field app by an operator (with photo, "machine stopped" toggle).

**US-P4-MNT-09 — Mechanic mobile flow (offline)** · *Persona: Mechanic* · Must · M
- AC1: Mechanic sees assigned WOs offline; starts/pauses/completes tasks; records labour time, parts used, meter reading, photos before/after.
- AC2: Component replacement within a WO updates installed base (removal + installation) upon WO verification.
- AC3: Mechanic signs completion; workshop chief verifies (signed).

**US-P4-MNT-10 — Work order costs** · Should · S
- AC1: Cost = labour (hours × rate, maskable) + parts (manual cost until P5) + external services.
- AC2: Cost per asset per period available to finance module (P7).

### EP-P4-05 Downtime tracking

**US-P4-MNT-11 — Downtime events** · Must · M
- AC1: Downtime recorded from: shift stoppage reason codes (P2), breakdown requests, WOs with downtime. Each with start/end, asset, cause category (mechanical, electrical, operational, weather, no material, planned maintenance).
- AC2: KPIs: availability %, MTBF, MTTR per asset and type.

### EP-P4-06 Procurement alerts

**US-P4-MNT-12 — Procurement trigger** · Must · M
- AC1: When `RUL_days ≤ lead_time + τ_buffer` (τ default 7 days, tenant-configurable) → ProcurementAlert created (one per component instance per cycle).
- AC2: Alert shows: component, asset, RUL, lead time, recommended order date, supplier, part numbers, quantity.
- AC3: Alert states: Open → Acknowledged → Ordered (PO reference) → Received → Closed / Dismissed (reason).
- AC4: In P4, stock-awareness is manual ("in stock" checkbox); in P5 it checks warehouses automatically.
- AC5: Notification to planner/storekeeper; overdue "order by" date escalates.

**US-P4-MNT-13 — Purchase request export** · Should · S
- AC1: Selected alerts → purchase request (PDF/Excel) for the customer's purchasing process/ERP.

### EP-P4-07 Maintenance grouping

**US-P4-MNT-14 — Grouped stop proposals** · *Persona: Maintenance Planner / Site Manager* · Should · M
- AC1: The system proposes groups of tasks/replacements on the same plant line or asset whose due windows overlap within a tolerance (default 5 days), minimizing number of stops (greedy algorithm v1).
- AC2: Proposal shows: stop date, tasks included, components replaced early (with % of life sacrificed), estimated downtime hours, production loss estimate (using nominal rate), versus separate stops.
- AC3: Planner can accept (creates scheduled WOs) or modify.

### EP-P4-08 Maintenance dashboards

**US-P4-ANA-01 — Wear trajectory charts** · Must · M
- AC1: Per component: accumulated usage vs time with projected line to end of life; order threshold line (`lead time + τ`) highlighted; ESTIMATED segments dashed.
- AC2: Per asset: all components' RUL as horizontal bars (sorted).

**US-P4-ANA-02 — Maintenance overview** · Must · M
- AC1: KPIs: availability, MTBF, MTTR, PM compliance %, backlog (open WOs by age), upcoming replacements (Gantt, 90 days), open procurement alerts.
- AC2: Filters: site, asset type, criticality.

### EP-P4-09 Upsell prompts

**US-P4-GRW-01 — Locked feature previews** · *Persona: Starter tenant admin* · Should · S
- AC1: Starter tenants see Maintenance menu with a preview screen built from their own data (e.g., "You have 34 machines and 1,240 operating hours this month. With Professional, Karry predicts wear-part replacements…").
- AC2: "Request upgrade" creates a sales lead in the platform console and notifies sales.

## 11.5 Technical scope

- Module `Maintenance` (ComponentType, InstalledComponent, PmPlan, WorkOrder, Downtime, ProcurementAlert, GroupingProposal); RUL domain service (pure, versioned); event handlers on `ShiftEntryVerified`, `MeterEventRecorded`.
- Nightly job: RUL recompute & alert generation; idempotent.
- Read models: `maintenance.rul_current`, `maintenance.kpi_daily`.
- Field app: mechanic role views, breakdown request; Portal: catalogue, installed base, PM plans, WO board (kanban + calendar), procurement alerts, dashboards.
- Data engineer: analytics schema, KPI definitions document (`docs/03-domain/kpi-definitions.md`).

**APIs:** `/api/v1/maintenance/component-types`, `/assets/{id}/components`, `/components/{id}/rul`, `/pm-plans`, `/work-orders` (+ actions), `/downtime`, `/procurement-alerts` (+ actions), `/grouping-proposals`, `/kpis`.

## 11.6 Test plan (Phase 4)

| Test ID | Description | Expected |
|---|---|---|
| TC-P4-MNT-001 | RUL with hours only, golden example | Matches hand computation |
| TC-P4-MNT-002 | RUL min of usage vs tonnage vs calendar | Correct metric chosen and shown |
| TC-P4-MNT-003 | < 7 days data | Type default rate used, flagged |
| TC-P4-MNT-004 | Meter failed 20 days | Estimated accumulation, alerts still fire |
| TC-P4-MNT-005 | Meter repaired, real reading lower than estimate | Reconciliation shown, history intact |
| TC-P4-MNT-006 | Abrasion class change | Recalculation, version recorded |
| TC-P4-MNT-007 | PM "whichever first" (250 h or 30 days) | Correct next due |
| TC-P4-MNT-008 | WO component replacement verified | Installed base updated, achieved life stored |
| TC-P4-MNT-009 | Mechanic offline completes WO | Syncs, signatures valid |
| TC-P4-MNT-010 | Procurement trigger at boundary (RUL = lead + τ) | Alert created exactly once |
| TC-P4-MNT-011 | Repeated nightly runs | No duplicate alerts |
| TC-P4-MNT-012 | Grouping: 3 components due within 4 days | One stop proposed with sacrifice % |
| TC-P4-MNT-013 | Availability/MTBF/MTTR golden dataset | Matches expert computation |
| TC-P4-GRW-001 | Starter tenant opens Maintenance | Preview with own data, lead created on request |

**Property tests:** RUL decreases monotonically with usage at constant rate; RUL ≥ 0 clamp; fallback estimate continuous at failure start.

**Mutation testing:** RUL & grouping services ≥ 75 % mutation score.

**Performance:** nightly RUL recompute for 50,000 components < 10 min; event-driven recompute < 2 s after shift verification.

**UAT:** pilot planner validates RUL of top 20 wear parts against their experience; workshop uses WOs for 2 weeks.

## 11.7 Deliverables

Component catalogue, RUL engine, PM plans, WO management + mechanic app, downtime KPIs, procurement alerts, grouping v1, dashboards, upsell previews, help articles, KPI definitions doc.

## 11.8 Demo script

1. Show a cone crusher mantle with RUL 23 days, lead time 30 days → procurement alert already raised with "order by" date in the past (red).
2. Mark the loader's hour meter as failed → alerts continue with ESTIMATED tags.
3. Planner accepts a grouped stop combining 3 replacements; WOs generated; mechanic completes one offline.
4. Maintenance overview KPIs.

## 11.9 Exit gate

- [ ] Pilot's top 20 wear parts tracked with RUL accepted by planner (within ±20 % of expert estimate or explained).
- [ ] At least one correct procurement alert raised in pilot.
- [ ] Mechanic app used for ≥ 80 % of pilot WOs over 2 weeks.

## 11.10 Risks

| Risk | Mitigation |
|---|---|
| Rated lives unknown for many parts | Seeded defaults + learn from achieved life; planner override |
| Customers want full CMMS features | Keep scope to quarry-critical; integration/export for advanced CMMS |

## 11.11 Commercial outcome

Professional's maintenance value is demonstrable. The upsell flow from Starter is live.

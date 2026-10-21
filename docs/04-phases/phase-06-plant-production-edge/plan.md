---
title: "Phase 6 — Plant, Production, Stockpiles & Edge Agent"
area: phases
tags: [phase-6, plant, mass-balance, stockpiles, edge-agent]
source: "PHASES_DETAILED_PLAN.md §13"
status: baseline
last_updated: 2026-10-21
---

# Phase 6 — Plant, Production, Stockpiles & Edge Agent

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 7 weeks (W32–W38)

## 13.1 Objectives & business value

Know **how much is produced, at what yield, where the bottleneck is**, and **automate weighbridge capture** to remove manual weight entry, which is the last big fraud vector. This phase reaches **M3 — Commercial v1**.

## 13.2 Scope

**In:** plant graph editor & revisions, materials size classes, node transfer functions, steady-state mass balance & transient simulation, blasts (pattern, PostGIS), production runs per shift with stoppages, conveyor capacity & degradation estimate v1, bottleneck detection, stockpiles (inflows/outflows/surveys/reconciliation), material reconciliation (blasted → crushed → sold + stock), site map, edge agent v1 for weighbridge indicators, production dashboards.
**Out:** OPC UA/PLC integration (P9), drone point cloud processing (P11), ML calibration (P7 sidecar).

## 13.3 Epics

| Epic | Title |
|---|---|
| EP-P6-01 | Plant graph modelling |
| EP-P6-02 | Mass balance & simulation engine |
| EP-P6-03 | Blasting & extraction |
| EP-P6-04 | Production runs & stoppages |
| EP-P6-05 | Conveyor capacity & bottlenecks |
| EP-P6-06 | Stockpiles & material reconciliation |
| EP-P6-07 | Site map (GIS) |
| EP-P6-08 | Edge agent v1 (weighbridge) |
| EP-P6-09 | Production dashboards |

## 13.4 User stories & acceptance criteria

### EP-P6-01 Plant graph modelling

**US-P6-PLN-01 — Plant graph editor** · *Persona: Site Manager* · Must · L→split
As a site manager, I want to draw my crushing plant by connecting machines, so that Karry computes my production and yields without any custom development.
- AC1: Drag-and-drop node palette: feeder, crusher (jaw/cone/impact/VSI), screen (n decks), washer/sand screw, hydrocyclone, conveyor, splitter, stockpile, waste.
- AC2: Each node can be linked to a registered asset (for maintenance/costs) or be virtual.
- AC3: Edges connect output ports (e.g., screen deck 1 oversize, deck 2 oversize, undersize) to inputs; edges can carry split ratios.
- AC4: Validation: every output routed; at least one feed; no dangling nodes; loops allowed only through crushers/screens (closed-circuit); errors displayed on canvas.
- AC5: Save as **revision** with effective date; previous revisions read-only; production data references the revision active at the time.
- AC6: Templates: "2-stage jaw+cone closed circuit", "3-stage with VSI", "sand washing" to start quickly.

**US-P6-PLN-02 — Node parameters** · Must · M
- AC1: Crusher: closed side setting (CSS), capacity (t/h nominal), product size distribution curve (from manufacturer table or default library) → breakage matrix.
- AC2: Screen: deck apertures, efficiency per deck (default 90 %), capacity (t/h per m²) → partition matrix.
- AC3: Conveyor: belt width, speed, trough angle, incline, length → nominal capacity ([§13.4](#134-user-stories--acceptance-criteria) EP-P6-05).
- AC4: Size classes defined per tenant (e.g., 0–4, 4–8, 8–16, 16–31.5, 31.5–63, >63 mm) and mapped to products.

### EP-P6-02 Mass balance engine

**US-P6-PLN-03 — Steady-state mass balance** · Must · M
- AC1: Given feed rate and feed PSD, compute flow (t/h and per size class) on every edge by solving `m = (I − Γ)⁻¹ f` (sparse); report circulating load per closed circuit, product rates per stockpile, yield per product.
- AC2: Non-convergent configuration (spectral radius ≥ 1) → explicit error identifying the loop.
- AC3: Mass conservation: Σ outputs = feed within 1e-6 relative (property test).
- AC4: Computation < 500 ms for 60 nodes.

**US-P6-PLN-04 — What-if simulation** · Should · M
- AC1: Change CSS, screen aperture, feed rate, or remove a node → see new yields and bottleneck, side-by-side with current.
- AC2: Transient simulation (time steps) for start-up / degradation scenarios, chart of flows over time.

**US-P6-PLN-05 — Calibration with actuals** · Should · M
- AC1: Compare model product rates with actuals (ticket outflows + stockpile change) per period; show model error %; allow manual adjustment of efficiencies; changes versioned.

### EP-P6-03 Blasting & extraction

**US-P6-PRD-01 — Blast record** · *Persona: Blasting Supervisor / Site Manager* · Must · M
- AC1: Blast: date, bench, pattern (burden, spacing, holes count, hole diameter, depth, subdrill, inclination), explosives used (kg per type), detonators, polygon (drawn on map), photos, signed by supervisor.
- AC2: Computes V_bank (B × S × H × N, or polygon area × H), tonnes (bank density), powder factor (kg/m³ and kg/t), drill metres ([Computational Engines (Corrected Mathematics) §10.2](../../03-domain/computational-engines.md#102-blast--yield-engine)).
- AC3: Safety fields (exclusion zone, all-clear time) recorded (HSE basics).

**US-P6-PRD-02 — Extraction / haulage to primary** · Should · M
- AC1: Truck loads from bench to primary crusher counted (by loader operator or dumper operator tap counter, offline) with load factor (m³ LOOSE per bucket/truck), converted to tonnes.
- AC2: Remaining blasted stock per blast tracked.

### EP-P6-04 Production runs

**US-P6-PRD-03 — Production run per shift** · *Persona: Plant Operator / Controller* · Must · M
- AC1: Per shift and plant: start/stop times, feed hours (from primary crusher shift entry), stoppages with reason codes and duration, feed tonnes (from belt scale reading if available, else estimated from haulage), signed and verified via the verification workflow.
- AC2: Plant availability & utilization % computed.

**US-P6-PRD-04 — Belt scale readings** · Should · S
- AC1: Manual entry (totalizer start/end + photo) per shift for belt scales where installed; later automatic via edge (P9).

### EP-P6-05 Conveyor capacity & bottleneck

**US-P6-PLN-06 — Conveyor nominal capacity** · Must · S
- AC1: Compute nominal capacity from cross-section area (width, trough, surcharge angle), speed, incline factor (CEMA/ISO 5048 table), material loose density.
- AC2: Show capacity vs modelled load (utilization %).

**US-P6-PLN-07 — Degradation estimate v1** · Should · M
- AC1: Rolling estimate of effective throughput factor φ = actual measured throughput / nominal, per conveyor with belt scale or per plant (using reconciliation), 14-day window; trend displayed; alert when φ drops > X % vs 90-day median.

**US-P6-PLN-08 — Bottleneck detection** · Must · S
- AC1: Highest sustained utilization node flagged as bottleneck in graph view (colour-coded utilization).

### EP-P6-06 Stockpiles & reconciliation

**US-P6-STK-01 — Stockpiles** · Must · M
- AC1: Stockpile per product per site (with polygon optional); inflows from production (model rates × run hours or measured), outflows from tickets and internal transfers.
- AC2: Book stock (t and m³ LOOSE) continuously computed.

**US-P6-STK-02 — Survey reconciliation** · Must · M
- AC1: Upload survey result (volume m³ per stockpile from drone/total station; CSV or manual), date, surveyor; system converts to tonnes (loose/compacted density as configured) and computes variance vs book.
- AC2: Variance within tolerance (default ±3 %) → book adjusted with approval; beyond → audit case.

**US-P6-STK-03 — Material reconciliation report** · Must · M
- AC1: Per period: blasted tonnes → fed to primary → produced per product → sold + internal use + stock change → unaccounted (t and %), with drill-down.

### EP-P6-07 Site map

**US-P6-PRD-05 — Site map** · Should · M
- AC1: MapLibre map with site boundary, benches, blast polygons (coloured by age), stockpiles, plant location, weighbridge; layers toggle; offline tile caching for site area on tablets (optional).

### EP-P6-08 Edge agent v1

**US-P6-EDG-01 — Edge agent install & enrollment** · *Persona: Karry field technician / Site IT* · Must · M
- AC1: Installer for Linux (Debian-based mini-PC) and Windows; enrollment with one-time code → mTLS client certificate issued; agent appears in portal with health.
- AC2: Auto-update with signed packages; rollback on failure.

**US-P6-EDG-02 — Indicator capture** · Must · L→split
- AC1: Supports serial (RS-232/485) and TCP indicators with configurable protocol parser (continuous output strings, request/response); at least 3 common indicator protocols at launch (decided from pilot hardware in P0).
- AC2: Stable weight detection (motion flag or stability window); weight pushed to the weighbridge screen in the portal/tablet in real time (WebSocket via cloud, or local network when cloud offline).
- AC3: Captured weight is signed by the agent key; ticket source = INDICATOR; manual override requires reason + flag.
- AC4: Local buffer (SQLite) when offline; ordered upload later; local web UI on LAN allows ticket issuing during internet outage (with the same signing).

**US-P6-EDG-03 — Peripherals** · Should · M
- AC1: Print tickets to local thermal printer; optional barrier relay control; optional camera snapshot at stable weight (stored with ticket).

### EP-P6-09 Production dashboards

**US-P6-ANA-01 — Production dashboard** · Must · M
- AC1: Tonnage rate curves per product and node (ṁ(t)), yield by fraction (stacked area), circulating load trend, plant availability/utilization, stoppage Pareto, bottleneck history, conveyor φ trend.
- AC2: Live graph view coloured by utilization.

## 13.5 Technical scope

- Module `Plant` (Plant, Revision, Node, Edge, SizeClassSet, MassBalanceResult), `Production` (Blast, Haulage, ProductionRun, Stoppage, BeltScaleReading), `Stockpiles` (Stockpile, Movement, Survey, Reconciliation).
- Math: sparse linear algebra in .NET (MathNet.Numerics, MIT licence); matrices built per revision and cached.
- Frontend: React Flow for graph editor; MapLibre GL; ECharts.
- Edge agent: .NET worker service (`Karry.EdgeAgent`), plugin architecture for protocols, SQLite buffer, local Kestrel UI, mTLS; packaged as .deb/.msi; device management endpoints in cloud.
- TimescaleDB hypertables for `production.flow_sample` and edge weight events.

**APIs:** `/api/v1/plant/plants`, `/plants/{id}/revisions`, `/revisions/{id}/balance`, `/revisions/{id}/simulate`; `/api/v1/production/blasts`, `/haulage`, `/runs`, `/stoppages`, `/belt-scales`; `/api/v1/stockpiles`, `/surveys`, `/reconciliation`; `/api/v1/edge/agents`, `/agents/{id}/config`, `/edge/v1/weights` (mTLS), WebSocket `/rt/weighbridge/{siteId}`.

## 13.6 Test plan (Phase 6)

| Test ID | Description | Expected |
|---|---|---|
| TC-P6-PLN-001 | Golden plant (domain expert spreadsheet) steady state | Flows within 0.5 % |
| TC-P6-PLN-002 | Random valid graphs (property) | Mass conserved |
| TC-P6-PLN-003 | Loop with γ → 1 | Non-convergence error naming the loop |
| TC-P6-PLN-004 | Revision change mid-month | Production attributed to correct revision |
| TC-P6-PLN-005 | Editor validation (dangling output) | Save blocked with highlight |
| TC-P6-PLN-006 | 60-node plant compute time | < 500 ms |
| TC-P6-PRD-001 | Blast volume & powder factor | Matches manual computation |
| TC-P6-PRD-002 | Production run verification | Goes through review queue, signatures valid |
| TC-P6-STK-001 | Book stock after production + tickets | Correct |
| TC-P6-STK-002 | Survey variance 2 % / 6 % | Adjust with approval / audit case |
| TC-P6-STK-003 | Material reconciliation totals | Balance closes; unaccounted computed |
| TC-P6-EDG-001 | Indicator simulator (3 protocols) | Stable weight parsed correctly |
| TC-P6-EDG-002 | Motion during weighing | No capture until stable |
| TC-P6-EDG-003 | Internet down 8 h | Local UI tickets, buffer, upload in order, numbering intact |
| TC-P6-EDG-004 | Tampered weight message (bad signature) | Rejected, security event |
| TC-P6-EDG-005 | Auto-update failure | Rollback to previous version |
| TC-P6-EDG-006 | mTLS cert revoked | Agent cannot connect |
| TC-P6-ANA-001 | Dashboard 12 months data | < 2 s |

**Hardware-in-the-loop:** edge agent tested with real indicator at pilot + serial simulator in CI (virtual COM ports).

**UAT:** pilot plant modelled; one month of model vs actuals compared; weighbridge auto-capture for 2 weeks.

## 13.7 Deliverables

Plant modelling & simulation, blasting, production runs, conveyors & bottlenecks, stockpiles & reconciliation, site map, edge agent v1 (packages, install guide, hardware compatibility list), production dashboards.

## 13.8 Demo script

1. Build a 2-stage closed-circuit plant from template; compute yields; show circulating load.
2. What-if: reduce cone CSS → yields shift, screen becomes bottleneck.
3. Record a blast on the map; powder factor computed.
4. Truck on weighbridge → stable weight appears automatically → ticket issued with source INDICATOR; unplug internet → continue locally → reconnect → synced.
5. Material reconciliation report for the month with unaccounted %.

## 13.9 Exit gate (M3 — Commercial v1)

- [ ] Pilot plant modelled; model vs actual product rates within agreed tolerance (e.g., ±10 %) over one month.
- [ ] Edge agent capturing ≥ 95 % of pilot tickets automatically for 2 weeks.
- [ ] Mass conservation & non-convergence tests green; mutation score ≥ 70 % for Plant engine.
- [ ] v1 release notes, hardware compatibility list, and installation services offering published.

## 13.10 Risks

| Risk | Mitigation |
|---|---|
| Lack of manufacturer PSD data | Default curves library + calibration with actuals |
| Indicator protocol diversity | Plugin architecture, protocol sniffing tool, partner with weighbridge installers |
| Edge hardware logistics in remote sites | Pre-configured hardware bundle shipped; remote support; partner installers |

## 13.11 Commercial outcome

**Commercial v1.** The product covers the full quarry value chain. The Business plan's plant features are ready, finance follows in P7. Edge agent add-on revenue and hardware bundles start.

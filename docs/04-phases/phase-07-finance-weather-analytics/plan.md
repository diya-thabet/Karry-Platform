---
title: "Phase 7 — Asset Finance, Weather & Analytics"
area: phases
tags: [phase-7, finance, dscr, weather, analytics]
source: "PHASES_DETAILED_PLAN.md §14"
status: baseline
last_updated: 2026-10-21
---

# Phase 7 — Asset Finance, Weather & Analytics

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 6 weeks (W39–W44) · data engineer + Python sidecar in production

## 14.1 Objectives & business value

Give owners and CFOs **financial truth per machine and per tonne**, plus **weather-aware planning and sales promises**. After this phase, **Business is sellable**, and the product speaks the language of the person who signs the subscription.

## 14.2 Scope

**In:** asset acquisition data, loans & schedules, residuals, cost rates (fuel, operator wages, maintenance, wear parts, insurance), TCO/h, cash OpEx/h, revenue attribution policies, net cash margin, DSCR (monthly/TTM), cost per tonne, multi-currency & FX, weather (forecast, climatology, rain-stop days, P_eff, delivery advisory), Python analytics sidecar (robust baselines, seasonal forecasting, degradation regression), full dashboard suite, scheduled reports, accounting export v1.
**Out:** general ledger, payroll; tax computations.

## 14.3 Epics

| Epic | Title |
|---|---|
| EP-P7-01 | Asset financing & loans |
| EP-P7-02 | Cost engine & cost rates |
| EP-P7-03 | Revenue attribution & profitability |
| EP-P7-04 | DSCR & solvency monitoring |
| EP-P7-05 | Multi-currency & FX |
| EP-P7-06 | Weather module |
| EP-P7-07 | Delivery & sales weather advisory |
| EP-P7-08 | Analytics sidecar (Python) |
| EP-P7-09 | Dashboard suite & scheduled reports |
| EP-P7-10 | Accounting export |

## 14.4 User stories & acceptance criteria

### EP-P7-01 Asset financing

**US-P7-FIN-01 — Asset acquisition & depreciation data** · *Persona: Finance* · Must · M
- AC1: Per asset: acquisition cost, date, currency, residual value, lifespan (hours and/or years), depreciation method for TCO (straight-line per hour or per year).
- AC2: All money fields maskable; access logged.

**US-P7-FIN-02 — Loans & schedules** · Must · M
- AC1: Loan: lender, principal, currency, start date, term, rate (fixed; variable with manual rate updates), type (annuity, linear, balloon, with grace period), day-count convention (30/360, ACT/365, ACT/360), payment frequency.
- AC2: Schedule generated (principal, interest, balance per period); import actual bank schedule as alternative (authoritative).
- AC3: A loan can finance one or several assets (allocation %).

### EP-P7-02 Cost engine

**US-P7-FIN-03 — Cost rates** · Must · M
- AC1: Cost rates with effective dates: fuel price per site (or derived from fuel deliveries WAC), operator wage per hour (by role or per operator), insurance per asset per month, overhead allocation keys.
- AC2: Maintenance cost per asset from WOs (labour + parts from inventory issues + external).

**US-P7-FIN-04 — TCO & cash OpEx per hour** · Must · M
- AC1: Per asset per period: depreciation/h, fuel cost/h (actual litres × price / hours), operator cost/h, maintenance & wear cost/h, insurance/h → TCO/h and cash OpEx/h ([Computational Engines (Corrected Mathematics) §10.7](../../03-domain/computational-engines.md#107-asset-cost--finance-engine)), with "show calculation".
- AC2: Same per asset type and per site.

**US-P7-FIN-05 — Cost per tonne** · Must · M
- AC1: Cost per tonne produced and per tonne sold per site per period, broken down by cost category (fuel, labour, maintenance, wear parts, depreciation, explosives, overhead).

### EP-P7-03 Revenue attribution & profitability

**US-P7-FIN-06 — Revenue attribution policies** · Must · M
- AC1: Tenant chooses policy per asset category: (a) by tonnes moved (loaders, trucks via haulage counts), (b) by productive hours share of the plant, (c) internal hire rate per hour, (d) none.
- AC2: Policy documented in UI and reports; changes versioned and recalculated for open periods only.

**US-P7-FIN-07 — Asset P&L** · Must · M
- AC1: Per asset per month: attributed revenue, cash OpEx, net cash margin, depreciation, TCO margin; trend 12 months.

### EP-P7-04 DSCR

**US-P7-FIN-08 — DSCR per asset & portfolio** · *Persona: CFO / Owner* · Must · M
As an owner, I want to know if each financed machine earns enough to pay its loan, so that I can decide to keep, sell or renegotiate.
- AC1: DSCR = net cash margin / debt service (principal + interest) for the same period; monthly & trailing-12-months; per asset (by allocation) and portfolio.
- AC2: Threshold configurable (default 1.25); breach → alert; status chip (Healthy ≥ threshold, Watch between 1.0 and threshold, Breach < 1.0).
- AC3: Chart: net cash margin vs debt service lines with rain season shading (from weather module).

### EP-P7-05 Multi-currency

**US-P7-FIN-09 — FX rates** · Must · S
- AC1: FX rates table (manual + optional provider feed), effective dates; conversions to functional currency at transaction date (costs) or period average (reports), configurable.
- AC2: Every converted amount shows original currency and rate on hover.

### EP-P7-06 Weather

**US-P7-WX-01 — Site weather data** · Must · M
- AC1: For each site location: 14-day forecast (daily precipitation, probability, temperature, wind), refreshed every 6 h; historical daily precipitation (≥ 10 years) for climatology.
- AC2: Provider adapter (Open-Meteo default); failures fall back to last data with staleness indicator.

**US-P7-WX-02 — Rain-stop day statistics** · Must · M
- AC1: Tenant defines rain-stop rule per site (e.g., precipitation ≥ 10 mm/day, or recorded stoppages with reason "weather" in shifts).
- AC2: Monthly R(m) distribution (P50/P90) from history; calibrated by site's actual weather stoppages over time.

**US-P7-WX-03 — Effective capacity** · Must · S
- AC1: P_eff(m) per [Computational Engines (Corrected Mathematics) §10.8](../../03-domain/computational-engines.md#108-weather--delivery-engine) using P_nominal from plant model (or historical average production), working days from site calendar.

### EP-P7-07 Delivery advisory

**US-P7-WX-04 — Order delivery advisory** · *Persona: Commercial Manager* · Must · M
- AC1: When creating/editing a large order (threshold configurable), show estimated completion date P50/P90 considering current order book, stock, P_eff and forecast; on-time probability for requested date with green/amber/red.
- AC2: Explanation text lists assumptions (rain days, capacity, committed orders).
- AC3: Back-test report over last rainy season (accuracy).

### EP-P7-08 Analytics sidecar

**US-P7-ANA-01 — Python analytics service in production** · *Persona: Engineer* · Must · M
- AC1: FastAPI service, stateless, called by workers via internal authenticated API (mTLS or signed JWT), tenant context passed explicitly; reads from read-replica or receives data payloads; never writes business tables directly (returns results to .NET which persists).
- AC2: Models: robust fuel baselines with seasonality (upgrade from P3 v1), conveyor degradation regression with weather covariate, production forecasting (seasonal).
- AC3: Model versioning; outputs carry model version; reproducible with pinned dependencies (uv lockfile).
- AC4: Monitoring: latency, errors, drift indicators.

### EP-P7-09 Dashboard suite & scheduled reports

**US-P7-ANA-02 — Finance dashboard** · Must · M
- AC1: Portfolio DSCR, assets by status, cost per tonne trend, cost breakdown, top/bottom 10 assets by margin.

**US-P7-ANA-03 — Scheduled reports** · Must · M
- AC1: Users subscribe to reports (daily production, weekly integrity, monthly finance) delivered by email (PDF + Excel), per their permissions and masking.

**US-P7-ANA-04 — Custom dashboard layouts** · Could · M
- AC1: Users can pin widgets to a personal home dashboard.

### EP-P7-10 Accounting export

**US-P7-FIN-10 — Accounting export v1** · Must · M
- AC1: Exports: sales tickets/invoices lines, fuel consumption by cost centre, inventory issues by cost centre, maintenance costs, with configurable account mapping (chart of accounts codes) and cost centres; CSV/Excel templates for common accounting systems.
- AC2: Export batches numbered and locked (re-export marks as duplicate).

## 14.5 Technical scope

- Modules `Finance` (AssetFinancing, Loan, Schedule, CostRate, AttributionPolicy, AssetPeriodResult), `Weather` (SiteForecast, ClimateSeries, RainStopRule, RainStats), `Reporting` (subscriptions, scheduler).
- Period closing concept for finance computations (open/closed months) to keep reports stable.
- Python `analytics/` service: FastAPI, Polars/pandas, statsmodels, scikit-learn; Docker image; contract-tested API (OpenAPI) consumed by .NET client.
- Read replica for analytics queries.

**APIs:** `/api/v1/finance/assets/{id}/financing`, `/loans`, `/loans/{id}/schedule`, `/cost-rates`, `/attribution-policies`, `/assets/{id}/pnl`, `/dscr`, `/cost-per-tonne`, `/fx-rates`; `/api/v1/weather/sites/{id}/forecast`, `/rain-stats`, `/capacity`; `/api/v1/sales/orders/{id}/delivery-advisory`; `/api/v1/reports/subscriptions`; `/api/v1/finance/exports`.

## 14.6 Test plan (Phase 7)

| Test ID | Description | Expected |
|---|---|---|
| TC-P7-FIN-001 | Annuity schedule vs bank reference | Match to the cent |
| TC-P7-FIN-002 | Day-count conventions | Correct interest per convention |
| TC-P7-FIN-003 | Balloon & grace period | Correct |
| TC-P7-FIN-004 | TCO/h golden asset | Matches CFO spreadsheet within 1 % |
| TC-P7-FIN-005 | Depreciation excluded from DSCR numerator | Verified |
| TC-P7-FIN-006 | DSCR breach | Alert + status chip |
| TC-P7-FIN-007 | Attribution policy change in closed period | Not recalculated |
| TC-P7-FIN-008 | FX conversion at transaction date vs period avg | Per config |
| TC-P7-FIN-009 | Masked finance fields for site manager | Never exposed incl. exports |
| TC-P7-WX-001 | Provider outage | Stale indicator, no crash |
| TC-P7-WX-002 | R(m) P50/P90 from 10y synthetic series | Correct quantiles |
| TC-P7-WX-003 | Delivery advisory back-test | Accuracy reported; P90 coverage ≥ 85 % |
| TC-P7-ANA-001 | Sidecar contract tests | .NET client & Python API compatible |
| TC-P7-ANA-002 | Sidecar down | Workers retry, last results kept, alert |
| TC-P7-ANA-003 | Model reproducibility | Same input + version → same output |
| TC-P7-RPT-001 | Scheduled report respects masking | Verified |
| TC-P7-EXP-001 | Export batch re-run | Marked duplicate, no double export |

**Property tests:** schedule balance reaches 0 at maturity; Σ principal = loan amount; DSCR invariant to currency conversion when all in same currency.

**UAT:** pilot CFO validates 3 assets' P&L and DSCR against own books; commercial manager uses delivery advisory on real orders.

## 14.7 Deliverables

Finance module, weather module, delivery advisory, analytics sidecar in production, finance & executive dashboards, scheduled reports, accounting export, finance user guide.

## 14.8 Demo script

1. CAT 966 loader: financing + loan schedule; P&L for 12 months; DSCR 0.92 in rainy months → breach alert.
2. Cost per tonne breakdown by site.
3. Commercial manager enters a 20,000 t order for next month → advisory: amber, 62 % on-time probability, suggests date for 90 %.
4. Monthly finance report delivered by email.

## 14.9 Exit gate

- [ ] CFO validation on 3 assets signed.
- [ ] Back-test of weather advisory documented.
- [ ] Sidecar SLOs met for 2 weeks.
- [ ] **Business plan published**.

## 14.10 Risks

| Risk | Mitigation |
|---|---|
| Revenue attribution disputes | Multiple policies, transparent method, CFO picks |
| Weather data quality in some regions | Use site-recorded stoppages to calibrate; allow manual rainfall entry (rain gauge) |

## 14.11 Commercial outcome

**Business sellable.** Karry now speaks to the CFO and the bank. A "bank-ready asset report" becomes a sales argument.

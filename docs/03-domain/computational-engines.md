---
title: "Computational Engines (Corrected Mathematics)"
area: domain
tags: [formulas, units, rul, mass-balance, fuel, dscr, weather]
source: "MASTER_PLAN.md §10"
status: baseline
last_updated: 2026-10-21
---

# Computational Engines (Corrected Mathematics)

> [Docs index](../README.md) › [03-domain](README.md)

All engines are **pure, deterministic domain services** in .NET with versioned formulas (`FormulaVersion`) so historical results are reproducible after formula changes. Every computed KPI stores its inputs snapshot hash for explainability.

## 10.1 Units & Conversion Engine

```
V_loose      = V_bank × (1 + s_swell(material))
M            = V_state × ρ_state(material) × (1 + w_moisture)
V_state      = M / (ρ_state(material) × (1 + w_moisture))
1 short ton  = 0.90718474 t
```

- Densities, swell factors and moisture defaults are **tenant-configurable per material**, with effective dates.
- Field UI toggle shows the converted value **and** the factors used (tap to see).
- **Invoicing basis is configurable per customer contract** (tonnes from weighbridge, or m³ derived from tonnes).

## 10.2 Blast & Yield Engine

```
V_bank_blast  = B × S × H_bench × N_holes            (or surveyed polygon area × H_bench)
M_raw         = V_bank_blast × ρ_bank × (1 + w)
η_yield (%)   = Σ M_product_i / M_raw × 100
Powder factor = explosive_kg / V_bank_blast           (kg/m³, standard KPI)
```

- Reconciliation: blasted tonnes vs. tonnes fed to primary crusher vs. tonnes sold + stockpile change. Variance shown as "unaccounted material".

## 10.3 Plant Graph & Mass Balance Engine

- The plant is a **directed graph** `G = (N, E)`; nodes are typed (`FEEDER`, `CRUSHER`, `SCREEN`, `WASHER`, `CONVEYOR`, `STOCKPILE`, `SPLITTER`), edges carry routing by **size class** and **deck**.
- Each node has a transfer function over a size-class vector `x ∈ ℝᵏ`:
  - Crusher: `y = B · x` (breakage matrix from closed-side setting / manufacturer curves).
  - Screen: `y_over = E · x`, `y_under = (I − E) · x` (partition / efficiency curve per deck).
- **Steady state:** assemble global linear system and solve `m = (I − Γ)⁻¹ f` (sparse LU). Fail fast with a clear error if the loop does not converge (spectral radius ≥ 1).
- **Transient / what-if:** discrete time stepping (the spec's `t − 1` formulation) for start-up and degradation scenarios.
- **Topology changes are versioned** (`PlantRevision`); production data always references the revision active at the time.
- UI: a node-based editor (React Flow) where managers drag machines and connect edges; validation prevents orphan outputs and unrouted fractions.

## 10.4 Conveyor Capacity Engine

```
Q_nominal  = A_cross(belt width, trough angle, surcharge angle) × v_belt × k_incline(θ)
Q_actual   = Q_nominal × φ_degradation(t)
```

- `φ_degradation(t)` is **estimated** (not typed in) from reconciling belt-scale / weighbridge throughput against nominal over rolling windows; weather influence learned as a covariate by the analytics sidecar.
- Bottleneck detection: node utilisation `u = m_in / capacity` across the graph; highest sustained `u` is the bottleneck.

## 10.5 Hybrid Maintenance & RUL Engine

For component `p` with tracked metric `m ∈ {HOURS, KM, DAYS}` and optional tonnage rating:

```
RUL_usage(p)   = (U_rating − U_accumulated) / (ū_daily × δ)        [days]
RUL_tonnage(p) = (M_rating − M_processed)  / (m̄_daily × δ)        [days]
RUL_days(p)    = min(RUL_usage, RUL_tonnage, RUL_calendar)
```

- `ū_daily` = robust (trimmed) mean of the last 30 operating days; configurable window.
- **Meter Fallback Switch:** when a meter is marked `FAILED`, the engine switches `U_accumulated` to `U_last_valid + ū_daily(30d) × days_since_failure`, flags all derived values as **ESTIMATED**, and opens a work order to repair the meter. When a new meter is installed, an **offset** is recorded (`MeterReplaced` event) so accumulated usage stays continuous.
- **Procurement trigger:** `RUL_days ≤ T_lead_time(part, supplier) + τ_buffer` (τ default 7 days) → `ProcurementTriggered`, deduplicated per component per cycle, considering stock already available in any warehouse (*Magasin Général* first).
- **Maintenance grouping (optimisation):** components whose RUL windows overlap within a tolerance are proposed for a single plant stop; objective = minimise downtime cost + premature replacement cost (greedy heuristic in v1; MILP via the Python sidecar later).
- **Phase 2 reliability:** fit Weibull (β, η) per component type × site from replacement history; RUL becomes a distribution with P10/P50/P90.

## 10.6 Fuel Analytics Engine

```
Consumption rate  r = L_dispensed / ΔH     (or / ΔKM, or / tonnes moved)
Baseline          r̃ = rolling median per (asset, activity type, season)
Robust z-score    z = 0.6745 × (r − r̃) / MAD
Anomaly if        (r − r̃)/r̃ > threshold (default 18 %)  AND  z > 3.5
```

Plus reconciliation checks: tank deliveries − dispensed − Δtank-dip; dispensed to an asset whose meters show no usage; dispensing outside shift; volume above tank capacity.

## 10.7 Asset Cost & Finance Engine

```
Depreciation/h  = (C_capital − S_residual) / H_lifespan
Cash OpEx/h     = F_conso × P_fuel + W_operator + M_maintenance (+ tyres, wear parts, insurance)
TCO/h           = Depreciation/h + Cash OpEx/h
Net cash margin = Revenue_attributed − Cash OpEx                   (per period)
DSCR            = Net cash margin / Debt service (principal + interest)  (per period)
Solvent         ⇔ DSCR ≥ threshold (default 1.25), monthly and trailing-12-months
```

- **Revenue attribution** to individual machines is a policy decision (by tonnes moved, hours on productive activity, or plant-level allocation) — configurable, documented, shown in UI.
- Loan schedules support annuity, linear, balloon, grace periods; interest computed with the contract's day-count convention.

## 10.8 Weather & Delivery Engine

```
N_work(m)   = N_total(m) − N_non_working(m)          (Sundays, holidays from site calendar)
R(m)        = expected rain-stop days (climatology P50; forecast overrides for the next 14 days)
P_eff(m)    = P_nominal × (1 − (R(m) + σ_safety) / N_work(m))
T_delivery  = Q_order / P_eff  →  rolled forward across months, net of already-committed orders
```

- Commercial advisory: given an order's quantity and date, compute **probability of on-time delivery** using the P50/P90 rain distribution and current order book. Shown as green/amber/red with explanation.

## 10.9 Inventory Variance Engine

```
Expected   = Stock_initial + Receipts − Issues ± Transfers
ΔStock     = Stock_physical − Expected
ΔValue     = ΔStock × weighted average cost
Flag       ⇔ |ΔStock| > tolerance(item category)  OR  |ΔValue| > value threshold
```

Responsibility attribution: the storekeepers in charge of the warehouse during the period (from duty assignments) are listed on the audit case — **not** auto-accused.

---
title: "Formula Handbook: Worked Numeric Examples"
area: domain
tags: [formulas, worked-examples, golden-tests, units, blast, mass-balance, conveyor, rul, fuel, dscr, weather, inventory]
source: "Phase 0 WP-P0-04"
status: draft
last_updated: 2026-10-21
---

# Formula Handbook: Worked Numeric Examples

> [Docs index](../README.md) › [03-domain](README.md)

Every formula in [computational-engines.md](computational-engines.md) with **definitions, units, valid ranges and a worked example**. Each example has a stable ID (`GE-nn`, "golden example"). Phase 1+ unit tests **must** use these exact inputs and expected outputs as hand-computed literals (see [llm_guidelines.md §8](../00-llm/llm_guidelines.md#8-test-quality-rules-faithful-senior-grade-tests): never re-derive expected values from the implementation).

> **Status: DRAFT.** Values were computed to full precision and rounded half-up as shown. Input parameters (densities, swell factors, ratings, prices) are **realistic placeholders**, not customer data. WP-P0-04 requires sign-off by a quarry/mining engineer (table at the end) before the status becomes `baseline`.

**Conventions.** Rounding: half-up at the last displayed digit; computations use full precision (decimal arithmetic) and round only for display. Money in `numeric(19,4)`, displayed to 2 decimals. Tonnes `t` are metric tonnes.

---

## 1. Units & material state (engine §10.1, ADR-0010)

**Formulas**
- `V_loose = V_bank × (1 + s)`
- `M = V_state × ρ_state × (1 + w)`  ⇔  `V_state = M / (ρ_state × (1 + w))`
- `1 short ton = 0.90718474 t`

| Symbol | Meaning | Unit | Valid range |
|---|---|---|---|
| `s` | swell factor | – | 0.10 – 0.80 (blasted hard rock ≈ 0.5–0.7) |
| `ρ_state` | dry density for the state (bank / loose / compacted) | t/m³ | 1.2 – 3.2 |
| `w` | moisture content (mass fraction) | – | 0 – 0.25 |

**GE-01: invoice volume from weighbridge mass**
Gravel 8/16, LOOSE dry density `ρ = 1.50 t/m³`, moisture `w = 0.02`. Truck net mass `M = 28.40 t`.
`V_loose = 28.40 / (1.50 × 1.02) = 28.40 / 1.53 =` **18.562 m³ LOOSE**

**GE-02: loose to bank via swell factor**
Blasted limestone, `s = 0.60`. A haul of `18.562 m³ LOOSE` corresponds to
`V_bank = 18.562 / 1.60 =` **11.601 m³ BANK**

**GE-03: forbidden operation**
`10 m³ BANK + 5 m³ LOOSE` → **rejected** (incompatible states; convert explicitly first).

---

## 2. Blast volume, mass, powder factor & yield (engine §10.2)

**Formulas**
- `V_bank = B × S × H × N`
- `M_raw = V_bank × ρ_bank × (1 + w)`
- `PF_vol = explosives_kg / V_bank` (kg/m³); `PF_mass = explosives_kg / M_raw` (kg/t)
- `L_hole = (H + subdrill) / cos(α)` where `α` = hole inclination from vertical
- `η_yield = Σ M_product / M_raw × 100 %`

**GE-04: blast**
`B = 3.0 m`, `S = 3.5 m`, `H = 12 m`, `N = 40` holes, `ρ_bank = 2.65 t/m³`, `w = 0.02`, explosives `3,200 kg`, subdrill `1.0 m`, `α = 10°`.

| Result | Computation | Value |
|---|---|---|
| Bank volume | 3.0 × 3.5 × 12 × 40 | **5,040 m³ BANK** |
| Raw mass | 5,040 × 2.65 × 1.02 | **13,623.12 t** |
| Powder factor (volume) | 3,200 / 5,040 | **0.635 kg/m³** |
| Powder factor (mass) | 3,200 / 13,623.12 | **0.2349 kg/t** |
| Drill length per hole | 13 / cos 10° | **13.2005 m** |
| Total drill metres | 13.2005… × 40 | **528.02 m** |

**GE-05: yield**
Saleable products from this blast: `Σ M_product = 12,400 t` → `η = 12,400 / 13,623.12 =` **91.02 %**

> Note: the original codex formula `A × h × cos(θ)` is **not** used (see [spec-review-corrections.md](spec-review-corrections.md) M1).

---

## 3. Closed-circuit mass balance (engine §10.3)

**Formula (steady state):** `m = (I − Γ)⁻¹ f`. For a single crusher–screen loop where the screen returns a fraction `γ` of its feed to the crusher:
`screen_feed = f / (1 − γ)`, `recirculation = screen_feed − f`, `circulating load ratio = recirculation / f`.

**GE-06: cone crusher closed circuit**
Fresh feed `f = 200 t/h`, screen oversize return `γ = 0.30`.

| Result | Computation | Value |
|---|---|---|
| Screen feed | 200 / 0.70 | **285.714 t/h** |
| Recirculating load | 285.714 − 200 | **85.714 t/h** |
| Circulating load ratio | 85.714 / 200 | **42.86 %** |
| Products leaving the circuit | conservation | **200 t/h** (= feed) |

**GE-07: non-convergence**
`γ = 1.0` (screen returns everything) → spectral radius ≥ 1 → engine must return a **non-convergence error naming the loop**, never a number.

---

## 4. Conveyor capacity & degradation (engine §10.4)

**Formulas**
- `Q_nominal = A_cross × v × k_incline` (m³/s); `A_cross` from belt width, trough angle and surcharge angle tables (CEMA / ISO 5048)
- Mass rate `= Q × 3600 × ρ_loose` (t/h)
- `φ = measured throughput / nominal throughput`

**GE-08: nominal capacity**
`A_cross = 0.080 m²`, `v = 2.0 m/s`, `k_incline = 0.95`, `ρ_loose = 1.60 t/m³`.
`Q = 0.080 × 2.0 × 0.95 = 0.152 m³/s =` **547.2 m³/h** → **875.52 t/h**

**GE-09: degradation factor**
Belt scale shows a sustained `700 t/h` at full feed → `φ = 700 / 875.52 =` **0.7995**

---

## 5. Hybrid RUL & procurement trigger (engine §10.5)

**Formulas**
- `RUL_usage = (U_rating − U_accumulated) / (ū_daily × δ)` (days)
- `RUL_tonnage = (M_rating − M_processed) / (m̄_daily × δ)` (days)
- `RUL_days = min(RUL_usage, RUL_tonnage, RUL_calendar)`; the limiting metric is reported
- Procurement alert ⇔ `RUL_days ≤ T_lead + τ_buffer`
- Meter fallback: `U_estimated = U_last_valid + ū_daily(30 d before failure) × operating_days_since_failure`, tagged ESTIMATED

**GE-10: hours only**
Jaw liner rated `1,200 h`, accumulated `900 h`, `ū_daily = 10 h/day`, `δ = 1.5`.
`RUL_usage = 300 / 15 =` **20 days** (limiting metric: HOURS)

**GE-11: hours vs tonnage**
Same liner also rated `300,000 t`, processed `240,000 t`, `m̄_daily = 2,500 t/day`, `δ = 1.5`.
`RUL_tonnage = 60,000 / 3,750 = 16 days` → `RUL_days = min(20, 16) =` **16 days** (limiting metric: TONNAGE)

**GE-12: procurement trigger**
Lead time `30 days`, `τ_buffer = 7 days` → threshold `37 days`. `16 ≤ 37` → **alert raised**. Recommended order date = today − 21 days → shown as **"order by date already passed (21 days late)"**.

**GE-13: meter fallback**
Hour meter fails at `900 h`; 30-day average before failure `10 h/day`; 6 operating days later:
`U_estimated = 900 + 10 × 6 =` **960 h (ESTIMATED)**. Alerts continue using 960 h.

---

## 6. Fuel consumption anomaly (engine §10.6)

**Formulas**
- `r = L_dispensed / ΔH` (L/h) for a refuel-to-refuel interval
- Baseline `r̃` = rolling median of the asset's last N intervals; `MAD` = median absolute deviation
- `z = 0.6745 × (r − r̃) / MAD`
- Anomaly ⇔ `(r − r̃) / r̃ > 18 %` **AND** `z > 3.5`

**GE-14: anomaly**
`410 L` dispensed over `ΔH = 21.5 h`; baseline `r̃ = 15.8 L/h`, `MAD = 0.6 L/h`.

| Result | Value |
|---|---|
| Rate `r` | **19.0698 L/h** |
| Deviation | **+20.69 %** (> 18 %) |
| Robust z | **3.676** (> 3.5) |
| Decision | **Anomaly (RULE-FUEL-05)** |

**GE-15: boundary behaviour (for tests)**
With `r̃ = 15.8`, deviations of `17.99 %` → no flag; `18.00 %` → no flag (strictly greater); `18.01 %` with `z > 3.5` → flag.

**GE-16: tank reconciliation (RULE-FUEL-04)**
Opening `20,000 L` + deliveries `30,000 L` − dispensed `41,800 L` − closing dip `8,050 L` = **150 L** unexplained, i.e. `150 / 41,800 =` **0.359 %** of throughput → within the 0.5 % tolerance → **no flag**.

---

## 7. Asset cost, TCO and DSCR (engine §10.7, ADR-0011)

**Formulas**
- `Depreciation/h = (C_capital − S_residual) / H_lifespan`
- `Cash OpEx/h = F_conso × P_fuel + W_operator + M_maintenance`
- `TCO/h = Depreciation/h + Cash OpEx/h`
- Annuity payment `= P × i / (1 − (1 + i)^−n)` (monthly rate `i`)
- `DSCR = (attributed revenue − cash OpEx) / debt service`; Healthy ≥ 1.25, Watch 1.00–1.25, Breach < 1.00

**GE-17: hourly cost of a wheel loader**
Capital `250,000 €`, residual `50,000 €`, lifespan `12,000 h`; fuel `18 L/h × 1.25 €/L`; operator `12 €/h`; maintenance `9 €/h`.

| Result | Value |
|---|---|
| Depreciation/h | **16.6667 €/h** |
| Cash OpEx/h | 22.50 + 12 + 9 = **43.50 €/h** |
| TCO/h | **60.1667 €/h** |

**GE-18: loan annuity**
Principal `200,000 €`, `6 %` nominal annual (`i = 0.005` monthly), `60` months → payment **3,866.56 €/month**; month 1 = interest **1,000.00 €** + principal **2,866.56 €**.

**GE-19: DSCR, normal month**
`180 h` worked, attributed revenue `14,000 €`, cash OpEx `43.50 × 180 = 7,830.00 €` → net cash margin `6,170.00 €` → `DSCR = 6,170.00 / 3,866.56 =` **1.596 → Healthy**

**GE-20: DSCR, rainy month**
`110 h`, revenue `8,600 €`, cash OpEx `4,785.00 €` → margin `3,815.00 €` → `DSCR =` **0.987 → Breach (RULE-FIN-01)**

> Depreciation is **excluded** from the DSCR numerator (it is not a cash cost and the loan already finances the asset).

---

## 8. Weather-adjusted capacity & delivery (engine §10.8)

**Formulas**
- `N_work = N_total − N_non_working`
- `P_eff = P_nominal × (1 − (R + σ_safety) / N_work)`
- `T_delivery = Q_order / P_eff` (working days, rounded **up** for planning)

**GE-21: order delivery estimate**
Month with `30` days, `4` non-working days → `N_work = 26`; expected rain-stop days `R = 6` (P50), safety `σ = 1`; `P_nominal = 1,200 t/day`; order `15,000 t`.
`P_eff = 1,200 × (1 − 7/26) =` **876.92 t/day** → `T = 17.105` → **18 working days**

---

## 9. Inventory variance (engine §10.9)

**Formulas**
- `Expected = Initial + Receipts − Issues ± Transfers`
- `ΔStock = Physical − Expected`; `ΔValue = ΔStock × WAC`
- Flag ⇔ `|ΔStock| / Expected > tolerance(category)` OR `|ΔValue| > value threshold`

**GE-22: hydraulic oil count**
Initial `400 L`, receipts `1,000 L`, issues `1,150 L` → expected `250 L`; physical `238 L`.
`ΔStock = −12 L` = **−4.80 %**; WAC `3.40 €/L` → `ΔValue =` **−40.80 €**. Lubricant tolerance `±1 %` → **audit case (RULE-INV-01)**.

---

## Golden example → future test mapping

| Golden example | Engine | First test owner |
|---|---|---|
| GE-01 … GE-03 | Units | US-P1-UNI-01 |
| GE-04, GE-05 | Blast & yield | US-P6-PRD-01 |
| GE-06, GE-07 | Mass balance | US-P6-PLN-03 |
| GE-08, GE-09 | Conveyor | US-P6-PLN-06 / 07 |
| GE-10 … GE-13 | RUL & procurement | US-P4-MNT-04 / 05 / 12 |
| GE-14 … GE-16 | Fuel | US-P3-FUEL-04 / 07 |
| GE-17 … GE-20 | Cost & DSCR | US-P7-FIN-02 / 04 / 08 |
| GE-21 | Weather | US-P7-WX-03 / 04 |
| GE-22 | Inventory | US-P5-INV-17 |

## Sign-off (WP-P0-04)

| Section | Reviewer (domain expert) | Date | Result / comments |
|---|---|---|---|
| 1 Units | | | |
| 2 Blast & yield | | | |
| 3 Mass balance | | | |
| 4 Conveyor | | | |
| 5 RUL | | | |
| 6 Fuel | | | |
| 7 Cost & DSCR | | | |
| 8 Weather | | | |
| 9 Inventory | | | |

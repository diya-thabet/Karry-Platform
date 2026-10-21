---
title: "Specification Review & Corrections"
area: domain
tags: [spec, corrections, formulas, legal, missing-requirements]
source: "MASTER_PLAN.md §3"
status: baseline
last_updated: 2026-10-21
---

# Specification Review & Corrections

> [Docs index](../README.md) › [03-domain](README.md)

A professional build starts by fixing the specification. The items below are **mandatory corrections** unless domain experts provide evidence otherwise. Each will be validated in Phase 0.

## 3.1 Mathematical & physical model corrections

| # | Spec item | Issue | Correction |
|---|---|---|---|
| M1 | `V_rock = A_pattern × h_bench × cos(θ)` | Blast volume is not reduced by `cos(θ)` of the face angle. For a blast pattern the in-situ (bank) volume is burden × spacing × bench height × number of holes. Inclined holes change drill length, not the bank volume per hole. | `V_bank = B × S × H_bench × N_holes` (or the surveyed polygon area × bench height). Drill length = `(H_bench + subdrill) / cos(α_hole)`. Best: reconcile against drone/survey volumes (PostGIS). |
| M2 | Unit toggle `m³ ↔ Tonnes` | **Missing the material state.** A cubic metre in the bench (bank), in a truck (loose), and in a compacted fill are different quantities. Ignoring swell factor will misprice every truck load by 30–60 %. | Every volume carries a **state**: `BANK`, `LOOSE`, `COMPACTED`. `V_loose = V_bank × (1 + s)` where `s` is the swell factor. Bulk density is per **material × state**. **Weighbridge mass is authoritative**; volumes are derived from mass, never the reverse, for invoicing. |
| M3 | `M_raw = V × ρ × κ_moisture` | Correct in form, but `ρ` must be the density matching the volume's state (see M2), and moisture should be recorded as a measured % where possible: `κ = 1 + w`. | `M = V_state × ρ_state(material) × (1 + w)` |
| M4 | Closed-loop recirculation with time iteration `t-1` | Time-stepping is fine for simulation, but operators want the **steady-state** balance. Also, a single scalar `γ` per crusher ignores particle-size distribution. | Model each node with a **partition/transfer matrix over size classes**. Steady state solves the linear system `m = f + Γ·m ⇒ m = (I − Γ)⁻¹ f` (valid when spectral radius of `Γ` < 1). Report **circulating load ratio**. Time-stepped simulation kept for "what-if". |
| M5 | `Q_belt(t) = Q_nom × φ_wear × ψ_incl × ω_weather` | The multiplicative factors are not physically derived and cannot be measured individually. Belt capacity follows `Q = A_cross × v × k_fill × k_incline` (CEMA / ISO 5048 / DIN 22101). | Use the standard capacity formula for **nominal** rating, then a **data-calibrated degradation factor** estimated from belt-scale or weighbridge reconciliation. Weather effect is a learned regression term, not a hand-set constant. |
| M6 | `RUL` uses `δ_abrasion` = "Bond abrasion index multiplier" | The Bond abrasion index `Ai` is a lab value (grams of metal loss), not a dimensionless multiplier ≥ 1. | Introduce a configurable **abrasion severity mapping** `δ = g(Ai)` per component type, calibrated over time. Phase 2+ moves to **Weibull** reliability models fitted on actual replacement history. |
| M7 | Loan solvency: `Σ(Revenue − C_ops·ΔH) > C_loan_annual / T` | **Dimensionally wrong.** An annual sum compared to annual debt service divided by the term. Also, `C_ops` includes depreciation, which double-counts capital already being repaid through the loan. | Use the bank-standard **Debt Service Coverage Ratio**: `DSCR = (Revenue − Cash OpEx) / Debt Service` over the same period (monthly and trailing-12-months). Solvent if `DSCR ≥ threshold` (configurable, typically 1.20–1.25). Depreciation is reported separately for TCO. |
| M8 | Fuel fraud: "+18 % above baseline" | A fixed threshold against a global baseline produces many false positives (load, terrain, idle time vary). | Per-asset, per-activity baseline (L/h, L/km, L/t) using **rolling median + MAD (robust z-score)**; 18 % is the default tenant-configurable threshold. Cross-check: dispensed fuel vs. tank-level deltas vs. ΔH vs. tonnage. |
| M9 | Inter-shift continuity `H_start,night ≡ H_end,day` | Correct as a rule but there are **legitimate exceptions** (meter replacement, reset/rollover, engine run for maintenance between shifts, towing for km). Flagging all as fraud destroys trust in alerts. | Strict equality by default; mismatch creates a **Continuity Exception** that must be resolved with a reason code (meter replaced, maintenance run, …) and second approval. Unresolved → fraud flag. |
| M10 | Stock variance `ΔStock ≠ 0 ⇒ audit flag` | For bulk / fluid goods (diesel, lubricants, aggregate stockpiles) zero variance is physically impossible (evaporation, temperature, survey precision). | Tolerance per item category (e.g., diesel ±0.5 %, stockpiles ±3 % via drone survey). Variance valued in currency. Flag only beyond tolerance. |
| M11 | Money / currencies | Not specified. | All money as `decimal(19,4)` + ISO-4217 currency; FX rates table with effective dates; reports in tenant functional currency. Never floating point. |

## 3.2 Terminology & legal corrections

| # | Spec item | Issue | Correction |
|---|---|---|---|
| T1 | "Native PKI electronic signature = canvas drawing + SHA-256" | A drawn image plus a hash is **not PKI**. PKI requires asymmetric key pairs bound to identities by certificates. A SHA-256 hash proves integrity only if the hash itself is protected. | Two layers: (1) **Handwritten signature image** (UX/evidence) and (2) **cryptographic signature** — ECDSA P-256 over the canonical payload hash, by a non-extractable device key + server KMS countersignature, optionally RFC 3161 timestamp. See [§13](../02-architecture/trust-layer-signatures-audit.md). Legal qualification (simple / advanced / qualified e-signature) must be confirmed per country with counsel. |
| T2 | "Immutable audit log" | Database tables are not immutable by declaration. | **Append-only tables** (no UPDATE/DELETE grants, trigger-enforced), **hash chain** per tenant, periodic **anchoring** of chain heads to WORM object storage (S3 Object Lock). Tamper-*evident*, honestly named. |
| T3 | "Python FastAPI math & graph engine" | Splitting core business math into a second runtime duplicates domain rules, doubles deployment surface, and creates consistency risk. The formulas are not NumPy-heavy. | **Core deterministic math lives in .NET** (single source of truth, unit + property tested). Python is used only where its ecosystem wins: forecasting, anomaly detection, Weibull fitting, optimisation. |
| T4 | ".NET 9", "PostgreSQL 16" | .NET 9 is a Standard-Term release reaching end of support in **November 2026**. Starting a new product on it is a mistake. | **.NET 10 LTS** (supported to Nov 2028). PostgreSQL 17 or 18 (decided in Phase 0 after verifying TimescaleDB/PostGIS extension support). |
| T5 | "Background Sync" | The Background Sync API is not available on iOS Safari and is unreliable on some Android WebViews. | Sync on app foreground, on `online` event, and on a timer; Background Sync is an enhancement only. |
| T6 | RLS policy example | `tenant_id = current_setting(...)` compares `uuid` to `text`; also unsafe with connection pooling if set at session level. | `USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)` + transaction-local `set_config(..., true)` inside each transaction (verified by spike WP-P0-12) + `FORCE ROW LEVEL SECURITY` + non-owner app role. See [§14.4](../02-architecture/security-identity-tenancy.md#144-multi-tenancy-implementation). |
| T7 | 8-week roadmap | Unachievable for this scope at production quality. | Phased plan in [§21](../04-phases/roadmap-summary.md). |

## 3.3 Missing requirements added by this plan

- **Sales & dispatch** (customers, price lists, orders, delivery notes, invoicing hand-off) — the weighbridge is meaningless without it.
- **Stockpile management** (finished-product inventory by fraction, survey reconciliation).
- **Fuel management** as its own module (tanks, bowsers, dispensers, deliveries, reconciliation).
- **HSE basics** (incident log, pre-start checklists) — expected by every quarry buyer and cheap to add on top of field forms.
- **Notifications** (in-app, push, SMS/WhatsApp for low-connectivity regions, email).
- **Data import/onboarding** (Excel templates for assets, parts, opening stock) — the #1 adoption blocker in SaaS sales.
- **Accounting export** (Sage, Odoo, QuickBooks, generic CSV/journal) — Karry is not an ERP and must not try to become one.
- **Tenant administration & billing** for SaaS (plans, seats, sites, usage metering).
- **Data protection compliance** (GDPR where applicable, local data-protection laws, data residency options).

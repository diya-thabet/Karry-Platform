---
title: "ADR-0025: Units engine in the shared kernel, materials in their own module, swell derived from densities"
area: adr
tags: [units, quantity, density, swell-factor, materials, conversion, mutation-testing]
source: "Phase 1 sub-phase P1.12"
status: proposed
last_updated: 2026-10-26
---

# ADR-0025 — Units engine in the shared kernel, materials in their own module, swell derived from densities

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (P1.12; the owner pre-approved merging green PRs, 2026-10-25)
- **Date:** 2026-10-26
- **Deciders:** Product owner, software architect
- **Related:** US-P1-UNI-01, US-P1-AST-04; [ADR-0010](0010-material-state.md) (material state, accepted);
  [computational engines §10.1](../03-domain/computational-engines.md#101-units--conversion-engine);
  [formula handbook §1](../03-domain/formulas-worked-examples.md#1-units--material-state-engine-101-adr-0010)
  (GE-01 … GE-03); [modular monolith](../02-architecture/modular-monolith-decision.md)

## Context

ADR-0010 settled *what* a quantity is: a value, a unit and, for a volume, its material state, with densities per
state and effective dates. Building it raises four questions ADR-0010 left open:

1. **Where the engine lives.** Weighbridge, plant, inventory, finance and the field app all handle quantities.
2. **Where materials live.** The story is in the Assets epic (US-P1-AST-04), but materials are not machines.
3. **Swell factor or loose density.** US-P1-AST-04 lists bank, loose and compacted densities *and* a swell factor.
   Entered independently, `ρ_loose` and `s` can disagree, and a bank → loose conversion then has two answers.
4. **Which units convert.** Litres and m³, or hours and days, look convertible but mean different things on a quarry.

## Decision

1. **The engine is in `Karry.SharedKernel.Units`:** `Unit`, `MaterialState`, the `Quantity` value object and
   `MaterialConversion`. It is pure (no I/O) and decimal-only, so every module and the tests use the same code. It is
   mutation-tested nightly (Stryker, break below 70 %).
   - `Quantity(value, unit, state?)`: m³ always has a state, and no other unit has one.
   - Arithmetic and comparison are allowed only within one dimension and, for m³, one state. Anything else throws
     (GE-03). The result keeps the left operand's unit.
   - **Units:** m³ (with state), t, kg, st (exactly 0.90718474 t), l, km, h, day, pcs.
     - Mass units convert exactly into each other.
     - **Litres are a liquid volume** (fuel, oil) and never become m³ of rock.
     - **A day is a calendar day** (rental, standing time), not 24 operating hours, so h and day never convert.
   - **Conversion** uses `M = V × ρ_state × (1 + w)`. A state change goes through mass. A measured moisture can
     replace the material's default. The result carries the **factors used** (densities, moisture, swell) for the
     "tap to see" display.
   - **Errors** are `units.not_convertible` and `units.density_missing` (with the `state` that has no density).
   - **On the wire** a quantity is `{"value": 18.562, "unit": "m3", "state": "loose"}`: lower-case codes, the value a
     JSON number read as decimal, and `state` null except for m³.
2. **Materials get their own `Materials` module** (schema `materials`), not the Assets module.
   - Weighbridge, plant, inventory and finance read materials through its public contract. They should not depend on
     the machines module to price a load.
   - This adds an 18th module to the modular-monolith list.
3. **The swell factor is derived, not stored:** `s = ρ_bank / ρ_loose − 1`, shown wherever densities are edited.
   - A material version stores up to three densities (bank, loose, compacted; a product that never sits in the bench
     has no bank density) and its default moisture.
   - The portal lets a person type the swell factor and computes the loose density from it, so the AC1 inputs are
     all there. There is still one source of truth, and a state change through mass always agrees with
     `V_loose = V_bank × (1 + s)`.
4. **Densities are versioned by effective date** (ADR-0010), on the company's clock (its settings' time zone, read through the Tenancy contract `TenantSummary.TimeZone`): materials are the same for every site. A conversion at a date uses the version valid on that
   date (TC-P1-UNI-002). Versions are never edited after their date, as with shift patterns (ADR-0024).

## Options considered

- **Swell stored next to the loose density, checked for consistency.** Rejected: it needs a tolerance (another
  number to agree on), and data entry fails on rounding.
- **Swell stored, loose density derived.** Rejected: products such as "Gravel 8/16" have a loose density (GE-01) and
  no bank density, so there is nothing to derive from.
- **Materials inside Assets.** Rejected for the coupling described in decision 2.
- **A units library (UnitsNet).** Rejected: it works in `double`, has no material state, and its generic units invite
  the liquid/solid and hours/days mistakes that decision 1 rules out.

## Consequences

- ✅ GE-01 … GE-03 pass exactly. 10,000 random round trips `t → m³ → t` stay within 1e-9 relative, and conversions are
  monotonic (TC-P1-UNI-001).
- ✅ Every screen can show how a number was obtained, from the factors the engine returns.
- ⚠ The handbook's swell factor (GE-02, s = 0.60) is entered as densities. The golden test uses 2.56 / 1.60 t/m³,
  whose ratio is exactly 1.60.
- ⚠ Valid ranges (density 1.2–3.2 t/m³, moisture 0–0.25, swell 0.10–0.80) come from the draft formula handbook. They
  change only with the domain expert's sign-off (WP-P0-04).

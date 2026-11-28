---
title: "Phase 3 Tracker"
area: phases
tags: [phase-3, tracker, status, sub-phases, exit-gate, carry-overs]
source: "Phase 3 execution plan (sub-phases.md)"
status: active
last_updated: 2026-11-27
---

# Phase 3 Tracker

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 3 plan](plan.md) · [Sub-phases](sub-phases.md)

Live status of Phase 3. The order, the content of each sub-phase and the way of working are in the
[sub-phase plan](sub-phases.md). Legend: ⬜ not started · 🟡 in progress · ✅ done.

## 1. Sub-phases

| # | Sub-phase | Status | Notes |
|---|---|---|---|
| P3.0 | Entry, decisions, golden datasets | ✅ | Merged #285 (plan #284). ADR-0042 … 0050 proposed. Generators `tools/demo/generate_fuel.py` (with the independent oracle `fuel_oracle.py`) and `generate_weighbridge.py` (with its plain rules); golden vectors `tests/vectors/fuel-golden-month.json`, `weighbridge-golden-days.json` with a drift guard; 31 tool tests (CI `tooling`). Ground truth: 0 false fuel anomalies on 39 seeds, the 25 % overconsumption caught every time, tank flags only on seeded days; weighbridge rules flag exactly the seeded tickets on 29 seeds. Found and decided: tank tolerance = max(0.5 %, dip uncertainty), litres rounded at the dip reading, MAD floor 1 % (ADR-0046, 0047). Waiting: GitHub Actions blocked by account billing (merge needs green CI) |
| P3.1 | Sales master data: customers, products, price lists | ✅ | Merged #286 (slices 1–3). Slice 1: module `Sales` (schema `sales`, RLS), customers with details, credit limit (`sales.customers.credit-limit`, masked), block with a reason / unblock, allowed sites, record versions. Slice 2: products (a sold material fraction via `IMaterialDirectory`, sale unit), price list versions per site and per customer (company's clock, never back-dated, frozen once in force), `IPriceBook.PriceAtAsync` and `GET /sales/prices` (customer beats site, per product; none = `sales.no_price`), unit price masked (`sales.prices.unit-price`). Slice 3a: Excel imports `customers`, `products` (TC-P3-SAL-004). Slice 3b: portal Settings › Customers, Products, Price lists (price on a date). Tests: Sales 34/34 (TC-P3-SAL-001…004), isolation 712/712, authorization 9/9, portal 72/72 |
| P3.2 | Trucks & haulers | ✅ | Slice 1 merged #287: module `Weighbridge` (schema `weighbridge`, RLS), haulers (name unique however written), trucks (plate as written + key of letters and digits, unique per company, TC-P3-WB-016; registered tare in kg with day and method; legal payload in kg; axle configuration), search by any part of the plate, unknown truck at the desk → `unverified` + RULE-WB-09 flag in the same transaction (TC-P3-WB-017), verify by someone other than the weighmaster (`weighbridge.trucks.verify`). Slice 2 (branch `feat/p3.2-trucks-portal`): Excel import `trucks` (plate however written, hauler by name, empty cells keep the truck's values, an unverified truck stays unverified, archived refused), portal Settings › Trucks (search as you type, status filter), truck form with Verify for the roles that may, Haulers; weighbridge error messages in the five languages. Tests: Weighbridge 17/17, portal weighbridge 8/8 |
| P3.Q | Code health (CodeScene) | 🟡 | Step 1 (branch `refactor/pq-fakeapi-router`): `testing/fakeApi.ts` (1,939 lines, health 2.84) split into `testing/fake/` — state, factories, HTTP helpers and 15 route modules; `fakeApi.ts` is now the dispatcher (203 lines) and re-exports the factories, so no test changed; portal 368/368. Next: `ShiftEntries.SignedRecord`, `IdentityModule`, shared import rows, `TenantIsolationSuite` |
| P3.3 | Ticket numbering (gap-free, offline) | ⬜ | Legal memo pending: `online_only` default |
| P3.4 | Scale tickets (server) | ⬜ | |
| P3.5 | Weighbridge desk (tablet and PC) | ⬜ | Usability session needed |
| P3.6 | Reversals, delivery notes, orders, daily close | ⬜ | |
| P3.7 | Weighbridge rules, ticket review, export | ⬜ | |
| P3.8 | Fuel storages, calibration, opening stock | ⬜ | |
| P3.9 | Fuel transactions: deliveries, dispenses, dips | ⬜ | |
| P3.10 | Tank reconciliation & fuel rules | ⬜ | |
| P3.11 | Consumption, baselines & anomaly detection | ⬜ | |
| P3.12 | Integrity cases, integrity & fuel dashboards | ⬜ | |
| P3.13 | Monthly savings & integrity report | ⬜ | |
| P3.14 | SMS & WhatsApp channels, quotas, fallback | ⬜ | Providers: owner (fakes until then) |
| P3.15 | Tenant export, support tooling, demo tenant, ROI | ⬜ | |
| P3.16 | Hardening, production, pilot & exit gate M2 (**a** local/CI, **b** production/pilot) | ⬜ | P3.16b needs the carry-overs |

## 2. Carry-overs

These items come from Phases 1 and 2 and **must be closed before P3.16b**.

| Item | From | Status |
|---|---|---|
| Real-tablet run (cold start, PIN unlock, QR scan) | P1.15 | ⬜ needs tablets |
| Environments through IaC (staging, production) | P1.17 | ⬜ needs a hosting provider |
| Staging deploy, rollback, restore drill | P1.18b | ⬜ needs P1.17 |
| Shadow run, device lab, Phase 2 exit gate | P2.12b | ⬜ needs P1.17, tablets, a pilot site |
| W-8: last reading includes shift entries | P2 UI review | ⬜ take before P3.9 |
| `?lang=` on the portal address (website language) | P2.13 | ⬜ |

## 3. Exit gate M2 (Pilot MVP)

| Criterion | Status |
|---|---|
| Pilot site runs ≥ 2 consecutive weeks with no paper for shifts, fuel and tickets | ⬜ |
| Zero data loss; no Sev-1 during pilot weeks; Sev-2 fixed within SLA | ⬜ |
| Fuel baseline for ≥ 80 % of pilot machines | ⬜ |
| Pilot customer agrees to convert (or to a case study) | ⬜ |
| Starter plan published with price book; MSA, DPA, SLA templates ready | ⬜ |

## 4. Decisions log

| # | Date | Decision |
|---|---|---|
| 1 | 2026-11-27 | Phase 3 build may start before the Phase 2 exit gate; carry-overs gate P3.16b ([sub-phases §0](sub-phases.md#0-can-phase-3-start-entry-decision)) |
| 2 | 2026-11-27 | `Sales` and `Weighbridge` as two modules; numbering `online_only` by default until the legal memo; PDFs through PDFsharp/MigraDoc (amendments A1–A8) |

---
title: "Product Scope, Personas & Use Cases"
area: product
tags: [personas, use-cases, scope]
source: "MASTER_PLAN.md §4"
status: baseline
last_updated: 2026-10-21
---

# Product Scope, Personas & Use Cases

> [Docs index](../README.md) › [01-product](README.md)

## 4.1 Personas

| Persona | Device | Primary jobs |
|---|---|---|
| **Field Operator** (loader, excavator, truck driver) | Low-cost Android tablet / phone, often offline | Start/end shift, meter readings + photo, fuel level, pre-start checklist, sign |
| **Weighmaster** (*pontier*) | Desktop/tablet at weighbridge | Issue scale tickets, capture truck, product, customer, sign |
| **Fuel Attendant** | Tablet | Record fuel dispensing per asset, tank dips |
| **Shift Controller** (*contrôleur*) | Tablet / desktop | Review, approve, reject, correct entries; resolve continuity exceptions |
| **Storekeeper** (*magasinier*) | Desktop | Receipts, issues, transfers, physical counts |
| **Maintenance Planner / Workshop Chief** | Desktop | Work orders, wear-part tracking, meter fallback, procurement triggers |
| **Site Manager** | Desktop / tablet | Plant topology, production KPIs, approvals escalation |
| **Commercial Manager** | Desktop | Customers, orders, weather-adjusted delivery promises |
| **Finance / CFO** | Desktop | Asset P&L, DSCR, cost per tonne, exports |
| **Executive / Owner** | Desktop / mobile | Dashboards, AI copilot, fraud alerts |
| **Tenant Admin** | Desktop | Users, roles, field masking, sites, settings |
| **Platform Operator** (Karry staff) | Internal console | Tenant provisioning, support, billing, health |

## 4.2 Core use cases (MVP-critical marked ★)

1. ★ Operator records shift start/end with meter photo, offline, and signs.
2. ★ Controller approves / rejects / corrects; every correction is audited with before/after.
3. ★ System detects inter-shift meter discontinuity and opens an exception.
4. ★ Weighmaster issues a signed scale ticket; corrections only by counter-signed reversal.
5. ★ Fuel attendant records dispensing; system computes consumption per hour/km/tonne and flags anomalies.
6. ★ Maintenance planner sees RUL per wear part and gets procurement alerts before lead time expires.
7. Manager switches a broken meter to calendar fallback; alerts continue.
8. Storekeeper transfers a part/asset from *Magasin Général* to a site store with a signed waybill; full lifetime history follows the item.
9. Storekeeper performs a semi-annual count; variances beyond tolerance raise an audit case.
10. Site manager models the crushing plant graph; system computes steady-state mass balance and yield.
11. CFO sees per-asset net margin vs. loan service (DSCR) with rain-season overlay.
12. Commercial manager gets a weather-adjusted delivery date for a large order.
13. Executive asks the copilot in French/English and gets a cited, RBAC-filtered answer.

## 4.3 Out of scope (explicitly)

- Full ERP/general ledger, payroll, HR.
- Mine planning / geological block modelling (integrate with Deswik/Surpac etc. later if demanded).
- Real-time machine control (Karry observes; it never actuates equipment).

---
title: "Alert & Detection Rule Catalogue"
area: domain
tags: [rules, fraud, alerts, thresholds]
source: "PHASES_DETAILED_PLAN.md §24"
status: baseline
last_updated: 2026-10-21
---

# Alert & Detection Rule Catalogue

> [Docs index](../../README.md) › [03-domain](../README.md)

| Rule | Description | Default threshold | Severity | Phase |
|---|---|---|---|---|
| RULE-FO-01 | Inter-shift meter continuity mismatch | Any difference without meter event | High | P2 |
| RULE-FO-02 | Negative or oversized ΔH/ΔKM | Δ < 0 or Δ > shift length + 0.5 h | High | P2 |
| RULE-FO-03 | Device clock skew | > 10 min unexplained | Medium | P2 |
| RULE-FO-04 | Capture outside site geofence | Outside polygon + 200 m | Medium | P2 |
| RULE-FO-05 | Photo without in-app capture marker | Any | High | P2 |
| RULE-FO-06 | Same photo hash reused | Any | High | P2 |
| RULE-FO-07 | Entry from revoked device | Any | Critical | P2 |
| RULE-FO-08 | Missing shift end | 1 h after shift end | Low | P2 |
| RULE-WB-01 | Tare deviation vs registered tare | > 3 % | Medium | P3 |
| RULE-WB-02 | Net above legal payload | > max payload | High | P3 |
| RULE-WB-03 | Impossible turnaround | < configured min minutes | High | P3 |
| RULE-WB-04 | Ticket outside operating hours | Outside site calendar | Medium | P3 |
| RULE-WB-05 | Manual weight without indicator photo | Any | High | P3 |
| RULE-WB-06 | Reversal rate anomaly per weighmaster | > 2× site median | Medium | P3 |
| RULE-WB-07 | Blocked customer override | Any | Medium | P3 |
| RULE-WB-08 | Manual override while indicator online | Any | High | P6 |
| RULE-FUEL-01 | Delivery vs dip variance | > 1 % | Medium | P3 |
| RULE-FUEL-02 | Dispense > asset tank capacity | > capacity + 5 % | High | P3 |
| RULE-FUEL-03 | Refuel meter inconsistent with shift entries | > tolerance | Medium | P3 |
| RULE-FUEL-04 | Tank reconciliation variance | > 0.5 % | High | P3 |
| RULE-FUEL-05 | Consumption anomaly | > 18 % and robust z > 3.5 | High | P3 |
| RULE-FUEL-06 | Refuel with no usage since last refuel | ΔH ≈ 0 | High | P3 |
| RULE-FUEL-07 | Refuel outside shift hours | Any | Medium | P3 |
| RULE-FUEL-08 | Frequent small refuels pattern | ≥ 3 in a shift | Medium | P3 |
| RULE-MNT-01 | Procurement trigger | RUL ≤ lead + 7 d | High | P4 |
| RULE-MNT-02 | PM overdue | Due date passed | Medium | P4 |
| RULE-MNT-03 | Unsafe asset (critical checklist fail) | Any | Critical | P2/P4 |
| RULE-INV-01 | Count variance beyond tolerance | Category tolerance | High | P5 |
| RULE-INV-02 | Transfer overdue | ETA + 2 days | Medium | P5 |
| RULE-INV-03 | Transfer discrepancy | Any | High | P5 |
| RULE-INV-04 | Negative stock after offline sync | Any | Medium | P5 |
| RULE-PRD-01 | Stockpile survey variance | > 3 % | High | P6 |
| RULE-PRD-02 | Conveyor degradation | φ drop > 15 % vs 90-day median | Medium | P6 |
| RULE-PRD-03 | Unaccounted material | > 5 % per month | High | P6 |
| RULE-FIN-01 | DSCR breach | < threshold | High | P7 |
| RULE-WX-01 | Order delivery at risk | P(on-time) < 70 % | Medium | P7 |
| RULE-TEL-01 | Manual vs telematics ΔH | > 0.5 h / shift | High | P9 |
| RULE-TEL-02 | Fuel dispensed vs telematics fuel used | > 10 % | High | P9 |
| RULE-TEL-03 | Machine outside geofence during shift | Any | Medium | P9 |
| RULE-TEL-04 | Engine running outside recorded shifts | > 15 min | High | P9 |
| RULE-OPS-01 | Device not synced | > 24 h | Medium | P2 |

All thresholds are tenant-configurable, except security rules (FO-05, FO-07), which can be adjusted in severity but not disabled.

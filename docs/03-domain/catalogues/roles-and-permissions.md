---
title: "Default Role & Permission Matrix"
area: domain
tags: [roles, permissions, rbac]
source: "PHASES_DETAILED_PLAN.md §23"
status: baseline
last_updated: 2026-10-21
---

# Default Role & Permission Matrix

> [Docs index](../../README.md) › [03-domain](../README.md)

Legend: **F** full, **R** read, **W** create/edit own, **A** approve, **M** masked (sensitive financial fields hidden), **—** none. Scope in brackets: (S) assigned sites, (W) assigned warehouses, (T) tenant-wide.

| Capability | Operator | Weighmaster | Fuel Att. | Mechanic | Storekeeper | Controller | Maint. Planner | Site Manager | Commercial | Finance | Executive | Tenant Admin |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Shift entries | W (own) | — | — | — | — | A (S) | R (S) | R/A (S) | — | R (T) | R (T) | R (T) |
| Checklists | W | — | — | R | — | R (S) | R (S) | F (S) | — | — | R | F |
| Continuity exceptions | R (own) | — | — | — | — | A (S) | R | A (S) | — | — | R | R |
| Scale tickets | — | W (S) | — | — | — | A reversals (S) | — | R (S) | R (T) | R (T) | R (T) | R |
| Prices on tickets | — | M or R* | — | — | — | M | — | R | F | F | R | F |
| Customers / orders | — | R (S) | — | — | — | — | — | R (S) | F | R | R | F |
| Fuel dispensing / dips | — | — | W (S) | — | — | A (S) | R | R (S) | — | R | R | R |
| Fuel anomalies | — | — | — | — | — | R (S) | R | F (S) | — | R | F | R |
| Work orders | Breakdown req. | — | — | W (assigned) | R | — | F (S) | A (S) | — | R costs | R | R |
| Components / RUL | — | — | — | R | R | — | F (S) | R (S) | — | R | R | R |
| Inventory movements | — | — | — | Receive sign | F (W) | — | R | R (S) | — | R | R | R |
| Transfers | — | — | — | — | F (W) | — | R | A (S) | — | R | R | R |
| Physical counts | — | — | — | — | W (W) | — | — | R | — | A | R | R |
| Plant graph | — | — | — | — | — | — | R | F (S) | — | R | R | R |
| Production runs / blasts | — | — | — | — | — | A (S) | R | F (S) | R | R | R | R |
| Cost rates, loans, P&L, DSCR | — | — | — | — | — | — | — | M | — | F | R | M/F* |
| Weather & delivery advisory | — | — | — | — | — | — | R | R | F | R | R | R |
| AI Copilot | — | — | — | — | — | — | R | R | R | R | F | Config |
| Users, roles, masking, policies | — | — | — | — | — | — | — | Users (S) | — | — | R | F |
| Billing & plan | — | — | — | — | — | — | — | — | — | R | R | F |
| Audit log / integrity check | — | — | — | — | — | R (S) | — | R (S) | — | R | R | F |

\* configurable per tenant. All roles can be cloned and customised (US-P1-IAM-05).

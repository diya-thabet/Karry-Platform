---
title: "Notification Catalogue"
area: domain
tags: [notifications]
source: "PHASES_DETAILED_PLAN.md §25"
status: baseline
last_updated: 2026-10-21
---

# Notification Catalogue

> [Docs index](../../README.md) › [03-domain](../README.md)

| Event | Default audience | Channels | Phase |
|---|---|---|---|
| Entry rejected | Operator | In-app, push | P2 |
| Review SLA breached | Escalation role | In-app, email | P2 |
| Continuity exception / fraud suspected | Controller, site manager, executive | In-app, email, WhatsApp/SMS (critical) | P2 |
| Unsafe asset | Site manager, maintenance | In-app, WhatsApp/SMS | P2 |
| Daily review digest | Site manager | Email | P2 |
| Ticket flag | Controller, commercial | In-app | P3 |
| Tank variance | Site manager, executive | In-app, WhatsApp/SMS | P3 |
| Fuel anomaly | Site manager | In-app, email | P3 |
| Monthly integrity & savings report | Executive | Email | P3 |
| Procurement alert / order-by date passed | Planner, storekeeper | In-app, email | P4 |
| PM due / overdue | Planner, workshop chief | In-app | P4 |
| WO assigned | Mechanic | Push | P4 |
| Transfer dispatched / overdue / discrepancy | Receiving storekeeper / managers | In-app, email | P5 |
| Count scheduled / overdue | Storekeeper, finance | Email | P5 |
| Survey variance / unaccounted material | Site manager, executive | In-app, email | P6 |
| Edge agent offline | Site manager, Karry support | In-app, email | P6 |
| DSCR breach | Finance, executive | In-app, email | P7 |
| Delivery risk on order | Commercial | In-app | P7 |
| Executive briefing | Executive | Email, in-app, WhatsApp (short) | P8 |
| Plan limit reached / payment failed | Tenant admin | In-app, email | P1/P10 |

Users can tune channels per notification type (except critical security notifications) and set quiet hours.

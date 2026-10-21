---
title: "Domain & Business Logic"
area: domain
tags: [domain, business-logic, formulas, glossary, rules, roles]
source: "created 2026-10-21"
status: baseline
last_updated: 2026-10-21
---

# 03-domain — Domain & Business Logic

> [Docs index](../README.md)

The quarry/mining business rules: what the system computes and enforces, independent of technology.

| File | Purpose |
|---|---|
| [spec-review-corrections.md](spec-review-corrections.md) | Corrections to the original specification (math, terminology, legal) and requirements that were missing. **Read before implementing any formula.** |
| [bounded-contexts.md](bounded-contexts.md) | Context map, bounded context catalogue (aggregates, events), shared value objects, asset lifetime history |
| [computational-engines.md](computational-engines.md) | Corrected formulas: units & conversion, blast & yield, plant mass balance, conveyor capacity, RUL, fuel analytics, asset cost & DSCR, weather & delivery, inventory variance |
| [glossary.md](glossary.md) | English/French domain glossary |

## catalogues/

| File | Purpose |
|---|---|
| [catalogues/roles-and-permissions.md](catalogues/roles-and-permissions.md) | Default roles × capabilities matrix with scopes and masking |
| [catalogues/detection-rules.md](catalogues/detection-rules.md) | Every `RULE-…` (fraud, integrity, maintenance, inventory, production, finance, telematics) with default thresholds, severity, phase |
| [catalogues/notifications.md](catalogues/notifications.md) | Notification events, audiences, channels, phase |
| [catalogues/documents-and-reports.md](catalogues/documents-and-reports.md) | Generated documents & reports, formats, signature and QR verification |

Planned (not yet written): `formulas-worked-examples.md` (WP-P0-04, signed by the domain expert), `kpi-definitions.md` (Phase 4).

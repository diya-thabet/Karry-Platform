---
title: "Quality Engineering & Testing Strategy"
area: quality
tags: [testing, qa, definition-of-done]
source: "MASTER_PLAN.md §20"
status: baseline
last_updated: 2026-10-21
---

# Quality Engineering & Testing Strategy

> [Docs index](../README.md) › [05-quality](README.md)

| Level | Tooling | Scope / Rules |
|---|---|---|
| Unit | xUnit, Vitest | All domain logic; ≥ 80 % coverage in Domain projects |
| **Property-based** | FsCheck | Every formula: unit round-trips (`t→m³→t`), mass conservation in plant graph (Σin = Σout at steady state), RUL monotonicity, DSCR invariants |
| **Mutation** | Stryker.NET | Math & trust modules, mutation score ≥ 70 % |
| **Golden datasets** | Domain-expert spreadsheets | Real quarry cases recomputed by engines must match to tolerance |
| Integration | Testcontainers (real PostgreSQL+extensions, Keycloak) | Repositories, RLS, migrations, outbox |
| **Tenant isolation** | Automated suite | Every endpoint called with tenant B token on tenant A data must 404/403 |
| Contract | OpenAPI diff in CI | Breaking change detection |
| E2E | Playwright | Critical journeys incl. **offline mode** (network emulation), multi-device sync, signature flow |
| Device lab | Real low-end Android tablets | Performance budgets, outdoor readability, battery |
| Load | k6 | Sync bursts (shift change at 06:00 / 18:00 across all sites), dashboards |
| Security | ZAP (DAST), pen test before GA, ledger tamper red-team exercise | — |
| AI evals | Golden Q&A sets | Correct tool selection, numeric fidelity, citation presence, FR/EN parity |
| UAT | Pilot site with real crews | Signed off by domain lead before each phase exit |

**Definition of Done (per story):** code + tests + docs updated, i18n strings (FR/EN), permissions defined, audit events emitted, telemetry added, offline behaviour defined (if field-facing), accessibility checked, reviewed and merged, deployed to dev.

---
title: "Repository Structure"
area: architecture
tags: [repository, monorepo, folders]
source: "MASTER_PLAN.md §26"
status: baseline
last_updated: 2026-10-21
---

# Repository Structure

> [Docs index](../README.md) › [02-architecture](README.md)

```
karry-platform/
├── docs/
│   ├── README.md                      docs index: map, lookup table, conventions
│   ├── 00-llm/                        AI-agent guidelines + hand-off memory
│   ├── 01-product/                    business model, pricing, personas, GTM, KPIs
│   ├── 02-architecture/               architecture, stack, security, data, sync, AI
│   ├── 03-domain/                     bounded contexts, formulas, glossary, catalogues
│   ├── 04-phases/                     roadmap + one folder per phase (plan, test reports, exit reviews)
│   ├── 05-quality/                    testing strategy, DoR/DoD, criteria, severity
│   ├── 06-operations/                 infra/DevOps, releases, runbooks/
│   ├── 07-management/                 team, risks, open questions
│   ├── 08-user-help/                  end-user help articles (fr/, en/)
│   ├── adr/                           architecture decision records
│   └── api/                           generated OpenAPI + guides (from Phase 1)
├── spikes/                            Phase 0 throwaway spikes with tests (rls-pooling, field-capture-pwa, signature-interop, pg-extensions)
├── src/
│   ├── backend/
│   │   ├── Karry.Host/                 ASP.NET Core composition root
│   │   ├── Karry.SharedKernel/         typed IDs, Money, Quantity, Result, events
│   │   ├── Karry.BuildingBlocks/       outbox, tenancy, auth, persistence, telemetry
│   │   ├── Modules/                    created with tools/new-module.sh <Name>
│   │   │   ├── Tenancy/  {Contracts,Domain,Application,Infrastructure,Api,Tests}
│   │   │   ├── Organization/
│   │   │   ├── Assets/
│   │   │   ├── FieldOps/
│   │   │   ├── Fuel/
│   │   │   ├── Verification/
│   │   │   ├── Trust/                  signatures + ledger
│   │   │   ├── Weighbridge/
│   │   │   ├── Stockpiles/
│   │   │   ├── Maintenance/
│   │   │   ├── Inventory/
│   │   │   ├── Plant/
│   │   │   ├── Finance/
│   │   │   ├── Weather/
│   │   │   ├── Analytics/
│   │   │   ├── AiCopilot/
│   │   │   └── Notifications/
│   │   ├── Karry.Workers/              background jobs host
│   │   └── Karry.EdgeAgent/            site edge worker
│   ├── frontend/
│   │   ├── apps/
│   │   │   ├── field/                  offline-first PWA (operators, weighmaster, fuel, store counts)
│   │   │   └── portal/                 controller, manager, executive, admin
│   │   └── packages/
│   │       ├── ui/                     design system (shadcn-based)
│   │       ├── charts/                 ECharts wrappers
│   │       ├── offline/                Dexie schema, outbox, sync client
│   │       ├── crypto/                 WebCrypto device keys, envelopes
│   │       ├── api-client/             generated from OpenAPI
│   │       └── i18n/                   FR/EN catalogs
│   └── analytics/                      Python FastAPI sidecar (uv, ruff, pytest)
├── tests/                              cross-cutting tests (module tests live in Modules/<Name>/Karry.Modules.<Name>.Tests)
│   ├── Karry.ArchitectureTests/        project-graph boundary rules (ADR-0001)
│   ├── Karry.Host.IntegrationTests/
│   ├── Karry.BuildingBlocks.UnitTests/
│   ├── Karry.IntegrationTests/
│   ├── Karry.TenantIsolationTests/
│   ├── e2e/                            Playwright
│   └── load/                           k6
├── deploy/
│   ├── docker/                         compose for local dev
│   ├── helm/
│   └── infra/                          OpenTofu
├── tools/
│   ├── new-module.sh                   create a module from the template and add it to Karry.slnx
│   ├── templates/karry-module/         dotnet new template (tested by test-template.sh, TC-P1-PLT-003)
│   ├── docs/check_links.py             docs link checker
│   └── ledger-verifier/                CLI to verify hash chains vs anchors
└── .github/workflows/
```

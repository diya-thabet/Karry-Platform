---
title: "Repository Structure"
area: architecture
tags: [repository, monorepo, folders]
source: "MASTER_PLAN.md §26"
status: baseline
last_updated: 2026-10-24
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
│   │   ├── Modules/                    created with tools/new-module.sh <Name>; Tenancy (tenant catalog, provisioning) since P1.5; Trust (audit ledger) since P1.6
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
│   ├── Karry.Host.IntegrationTests/    health, OpenAPI (Testcontainers PostgreSQL)
│   ├── vectors/                        rfc8785.json (+ generator): canonical-JSON reference shared by .NET and TypeScript
│   ├── Karry.Testing/                  shared test kit (not a test project): PostgresServer with the real roles, TestTokens issuer, MigrationLinter
│   ├── Karry.BuildingBlocks.UnitTests/ API conventions, module contract, observability (TC-P1-OPS-003/005)
│   ├── Karry.BuildingBlocks.IntegrationTests/ outbox, idempotency, end-to-end backbone (PostgreSQL)
│   ├── Karry.SharedKernel.UnitTests/   IDs, Money (property tests), Result, aggregates
│   ├── Karry.IntegrationTests/
│   ├── Karry.TenantIsolationTests/     every API operation attacked with another tenant's token (ci / tenant-isolation)
│   ├── Karry.AuthorizationTests/       every endpoint's permission declaration, masking suite (ci / authorization)
│   ├── e2e/                            Playwright
│   └── load/                           k6
├── deploy/
│   ├── docker/                         compose.yaml (local stack incl. Grafana LGTM), smoke-test.sh (TC-P1-PLT-004)
│   ├── database/                       roles.sql (karry_owner / karry_app, privileges; used by local stack, tests, environments), global-reference-tables.txt (tables without tenant RLS, with reasons; migration linter)
│   ├── observability/                  prometheus.yaml, alerts/ (rules + promtool tests), dashboards/ (generate.py → JSON), verify_stack.py
│   ├── helm/
│   └── infra/                          OpenTofu
├── .github/                            workflows (ci, nightly, docs, spikes), PR/issue templates, CODEOWNERS
├── tools/
│   ├── ci/                             coverage_gate.py, merge_guard.py (only merge path), install-tool.sh (checksummed tools)
│   ├── new-module.sh                   create a module from the template and add it to Karry.slnx
│   ├── templates/karry-module/         dotnet new template (tested by test-template.sh, TC-P1-PLT-003)
│   ├── docs/check_links.py             docs link checker
│   └── ledger-verifier/                karry-ledger CLI: verify a tenant hash chain (anchors comparison from P1.6 slice 4)
└── .github/workflows/
```

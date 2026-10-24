---
title: "Technology Stack"
area: architecture
tags: [stack, dotnet, react, postgresql, keycloak, python]
source: "MASTER_PLAN.md §7"
status: baseline
last_updated: 2026-10-24
---

# Technology Stack

> [Docs index](../README.md) › [02-architecture](README.md)

## 7.1 Stack overview

| Layer | Technology | Rationale | Alternatives considered |
|---|---|---|---|
| **Field & Web client** | React 19, TypeScript (strict), Vite | Mature ecosystem, team availability, PWA tooling | Angular (heavier), Flutter (no true PWA parity) |
| **Routing / data** | TanStack Router, TanStack Query | Type-safe routes, cache + offline-aware mutations | React Router, SWR |
| **UI** | Tailwind CSS v4, shadcn/ui (Radix primitives), Lucide icons | Accessible primitives, owned components, fast theming per tenant | MUI (heavy bundle), Mantine |
| **Forms / validation** | React Hook Form + Zod (schemas shared with API via OpenAPI codegen) | Performance on low-end devices, typed validation | Formik |
| **PWA / offline** | vite-plugin-pwa (Workbox), Dexie.js (IndexedDB), Web Locks API | Proven offline caching, typed IndexedDB, multi-tab safety | RxDB, PouchDB/CouchDB |
| **Signatures (client)** | signature_pad (vector strokes), WebCrypto (ECDSA P-256, non-extractable keys) | Standard, no native app needed | Native wrappers |
| **Charts** | Apache ECharts (time-series, heatmaps, large datasets via canvas/WebGL), Recharts for small widgets | ECharts handles 100k+ points smoothly; Recharts is SVG and slows beyond ~5k points | Highcharts (commercial), uPlot |
| **Maps** | MapLibre GL JS + PostGIS vector tiles (pg_tileserv / Martin) | Open source, offline tile caching possible | Mapbox (licensed), Leaflet |
| **i18n** | i18next + ICU message format | Pluralisation, FR/EN, RTL-ready | FormatJS |
| **Optional native shell** | Capacitor (Phase 7+) | Only if MDM / kiosk / Bluetooth scale / camera control demands it | — |
| **Backend runtime** | **.NET 10 LTS**, ASP.NET Core Minimal APIs + endpoint groups | Performance, strong typing, LTS to Nov 2028, great PostgreSQL support | Node/NestJS, Java/Spring |
| **ORM / data access** | EF Core 10 (Npgsql) for writes; Dapper / raw SQL for analytics reads | Productivity + control where needed | Marten (event store), Linq2DB |
| **Messaging (in-process)** | Domain events + **transactional outbox** in PostgreSQL; **in-house dispatcher** ([ADR-0018](../adr/0018-background-jobs-and-dispatcher.md)) | Reliable without extra infra in MVP. ⚠ MediatR, AutoMapper, MassTransit v9 moved to commercial licences in 2025 — avoid or budget | Wolverine; RabbitMQ / Kafka later if needed |
| **Background jobs** | **Quartz.NET** (PostgreSQL job store, clustered) in `Karry.Workers` ([ADR-0018](../adr/0018-background-jobs-and-dispatcher.md)) | RUL recompute, alerts, report generation, chain anchoring, usage snapshots | Hangfire |
| **Validation** | FluentValidation | Expressive server-side rules | DataAnnotations |
| **PDF / documents** | QuestPDF (check licence tier vs. revenue) | Scale tickets, waybills, shift reports | — |
| **Analytics/ML sidecar** | Python 3.13, FastAPI, NumPy, SciPy, pandas/Polars, statsmodels, scikit-learn, lifelines (Weibull) | Best ecosystem for forecasting & anomaly detection | ML.NET |
| **Database** | **PostgreSQL 17/18** | Reliability, RLS, JSONB, extensions | SQL Server |
| **Extensions** | PostGIS (spatial), TimescaleDB (telemetry hypertables, continuous aggregates), pgcrypto, pg_partman (if needed) | Spatial blast/stockpile, efficient time-series | Separate TSDB (InfluxDB) |
| **Cache** | Redis / Valkey | Sessions-free API, rate-limit counters, hot dashboard caches | — |
| **Object storage** | S3-compatible with Object Lock: cloud provider S3 (AWS S3 / Cloudflare R2 / Scaleway / OVH); **SeaweedFS** locally, in CI and on-prem ([ADR-0017](../adr/0017-local-stack-and-object-storage.md)) | Photos, signatures, PDFs, WORM audit anchors | MinIO (OSS archived 2026), RustFS (fallback) |
| **Search (later)** | PostgreSQL full-text first; OpenSearch only if required | Avoid premature infra | — |
| **Identity** | **Keycloak** (OIDC/OAuth 2.1, PKCE, MFA, organizations) | Self-hostable, SSO for enterprise tenants, standards | Zitadel, Auth0/Entra (cost, residency) |
| **Authorization** | In-app policy engine: RBAC + ABAC (site scope) + field-level masking | Fine-grained, tenant-configurable | OpenFGA (if relationships get complex) |
| **Secrets / keys** | Cloud KMS / HashiCorp Vault (transit engine) | Server signing keys never leave KMS | — |
| **AI** | AI Gateway (.NET) → Claude (default: `claude-sonnet-5-5`; `claude-haiku-5-5` for classification; `claude-opus-5-5` for deep analysis) or self-hosted open-weight model; pgvector for retrieval | Tool-calling, multilingual, provider-swappable | Direct SDK coupling (rejected) |
| **Weather data** | Open-Meteo (forecast + historical climatology) behind an adapter; commercial provider optional | Free tier, historical normals for `R(m)` | Meteomatics, Tomorrow.io |
| **Containers / orchestration** | Docker, Kubernetes (managed: AKS/EKS/GKE or Scaleway/OVH for EU/Africa residency) | Standard, portable, on-prem option | Azure Container Apps for early stage |
| **IaC** | OpenTofu (Terraform-compatible) + Helm | Reproducible environments | Pulumi |
| **CI/CD** | GitHub Actions, Renovate, trunk-based development, environment promotion | — | GitLab CI |
| **Observability** | OpenTelemetry SDKs → Grafana (Prometheus/Mimir, Loki, Tempo), Sentry for client errors | Vendor-neutral, full traces from tablet to DB | Datadog (cost) |
| **Quality** | xUnit v3 (Microsoft Testing Platform), CsCheck (property-based), Testcontainers, project-graph architecture tests + reflection-based type rules (P1.5; ArchUnitNET not needed), AwesomeAssertions, Playwright, Vitest, Storybook + axe, k6, Stryker.NET (mutation on math/trust) | — | — |
| **Security tooling** | gitleaks (secrets), Trivy (dependencies, misconfiguration), Semgrep CE (SAST), Roslyn CA analyzers, OWASP ZAP (DAST, P1.18), Renovate ([ADR-0021](../adr/0021-security-scanning-private-repo.md)) | Free and licence-compatible for a private repository | CodeQL / GitHub Code Security (paid on private repos; revisit with first customers), Snyk |

> Exact library versions are pinned in lock files ([ADR-0002](../adr/0002-dotnet-10-lts.md), [ADR-0020](../adr/0020-frontend-toolchain-versions.md)) and upgraded monthly via Renovate. The reference versions below were verified at the start of Phase 1.

## 7.1.1 Verified versions (Phase 1 start, 2026-10-22)

Checked against the official registries (NuGet, npm, Docker Hub, Quay, GitHub releases, dotnet release metadata) in sub-phase P1.0. These are the versions to start from; the lock files are the source of truth afterwards.

**Runtimes & tools**

| Component | Version | Notes |
|---|---|---|
| .NET SDK / runtime | 10.0.402 / 10.0.13 | LTS, support until 2028-11-14 |
| Node.js | 24 LTS (24.22.1) | [ADR-0020](../adr/0020-frontend-toolchain-versions.md); Node 26 when it becomes LTS |
| pnpm | 12.15.0 | via `packageManager` |
| TypeScript | 6.0.3 | 7.0.2 exists but `typescript-eslint` supports `<6.1.0` ([ADR-0020](../adr/0020-frontend-toolchain-versions.md)) |
| OpenTofu | 1.13.2 | IaC (P1.17) |
| oasdiff | 1.33.0 | CI `openapi-diff` (P1.5): breaking-change check of the API contract; checksum-verified install |
| actionlint / gitleaks / Trivy / Semgrep CE | 1.7.12 / 8.30.1 / 0.75.0 / 1.180.0 | CI (P1.2); installed with checksum verification by `tools/ci/install-tool.sh`; Semgrep image pinned by digest |

**Backend (NuGet)**

| Package | Version | Package | Version |
|---|---|---|---|
| Microsoft.EntityFrameworkCore | 10.0.13 | Npgsql / Npgsql.EntityFrameworkCore.PostgreSQL | 10.0.3 |
| Microsoft.AspNetCore.OpenApi | 10.0.13 | Scalar.AspNetCore (API reference UI) | 2.18.2 |
| AWSSDK.S3 (anchors, P1.6) | 4.0.104.4 | Quartz.Extensions.Hosting (P1.6) | 4.5.1 (4.6.0 < 7 days; 4.5.1 carries the October security-advisory dependency fixes) |
| Microsoft.EntityFrameworkCore.Design, dotnet-ef (local tool) | 10.0.13 (P1.5) | Microsoft.AspNetCore.Authentication.JwtBearer, Microsoft.Extensions.ApiDescription.Server (build-time OpenAPI) | 10.0.13 (P1.5) |
| Quartz.Extensions.Hosting | 4.6.0 | OpenFeature | 2.14.1 |
| OpenTelemetry.Extensions.Hosting, .Instrumentation.AspNetCore/.Http/.Runtime, .Exporter.OpenTelemetryProtocol/.InMemory | 1.20.0 (P1.4; 1.20.1 was < 7 days old on 2026-10-23) | Npgsql.OpenTelemetry | 10.0.3 |
| FluentValidation | 12.2.0 | ClosedXML (Excel import, P1.14) | 0.105.1 |
| Sentry.AspNetCore | 6.12.1 | CsCheck (property tests, P1.3) | 4.9.1 |
| xunit.v3 | 4.0.2 | Testcontainers.PostgreSql (Keycloak runs from the core `ContainerBuilder`, no extra package, P1.8) | 4.16.0 |
| Microsoft.Extensions.TimeProvider.Testing | 10.11.0 | ~~TngTech.ArchUnitNET.xUnitV3~~ (not used: type rules are plain reflection, P1.5) | n/a |
| AwesomeAssertions (Apache-2.0 fork of FluentAssertions) | 9.6.0 | UUIDv7 | built-in `Guid.CreateVersion7` (no package) |
| dotnet-stryker (mutation) | 5.0.0 | | |

**Frontend (npm)**

| Package | Version | Package | Version |
|---|---|---|---|
| react / react-dom | 19.3.1 | vite | 8.3.4 |
| @tanstack/react-router | 1.170.45 | @tanstack/react-query | 5.104.3 (P1.7: newest ≥ 7 days old) |
| tailwindcss (+ @tailwindcss/vite) | 4.3.3 | Radix primitives (dialog 1.3.1, select 2.4.1, toast 1.3.1, slot 1.5.0), cva 0.7.1, tailwind-merge 3.7.0, lucide-react 1.58.0 (P1.7; shadcn patterns written in-house, no CLI) | n/a |
| i18next / react-i18next (+ i18next-icu 2.5.0, intl-messageformat 12.1.3) | 26.4.7 / 17.0.20 (P1.7: newest ≥ 7 days old) | dexie | 4.4.6 |
| vite-plugin-pwa | 2.0.0 | maplibre-gl | 6.15.0 |
| vitest | 5.0.5 | @testing-library/react | 16.3.3 |
| @playwright/test / playwright | 1.64.1 | axe-core (in Vitest browser mode, P1.7) | 4.14.0 |
| storybook (+ react-vite, addon-a11y) | 10.6.2 (P1.7) | eslint / typescript-eslint | 10.12.0 / 8.71.2 |
| openapi-typescript / openapi-fetch (`@karry/api-client`, P1.7) | 7.13.0 / 0.17.0 (peer `typescript ^5` allowed for TS 6, output type-checked) |
| @sentry/react (error tracking, `@karry/observability`) | 11.8.0: `dataCollection` all off (v11 collects user, cookies, headers, bodies, query strings by default) | @openfeature/web-sdk / react-sdk | 1.10.0 / 1.4.1 |

**Container images (local stack, CI)**

| Image | Tag | Notes |
|---|---|---|
| `timescale/timescaledb-ha` | `pg17.11-ts2.31.0` (pg18 tag also verified) | PostGIS 3.6.4, TimescaleDB 2.31.0, pgvector 0.8.7, pgcrypto (WP-P0-13) |
| `quay.io/keycloak/keycloak` | 26.8.1 | Organizations feature for SaaS tenants; realm and theme as code in `deploy/keycloak` (P1.8) |
| `chrislusf/seaweedfs` | pin by digest in P1.1 (latest release 4.48) | S3 + Object Lock ([ADR-0017](../adr/0017-local-stack-and-object-storage.md)) |
| `valkey/valkey` | 9.0.6-alpine | Redis-compatible cache |
| `axllent/mailpit` | v1.31.6 | Local mail catcher |
| `grafana/otel-lgtm` | 0.37.0 | Local OpenTelemetry + Grafana stack (P1.4): Grafana 13.2.3, Prometheus 3.15.0 (its promtool tests the alert rules in CI), Tempo 3.1.0, Loki 3.7.8 |

**Supply-chain rule (P1.2):** npm packages must be **at least 7 days old** (`minimumReleaseAge` in `src/frontend/pnpm-workspace.yaml`, enforced at install) and CI tools at least one week old when pinned; hence vite 8.3.4, eslint 10.12.0, typescript-eslint 8.71.2 and Trivy 0.75.0 instead of the newest releases.

**Dropped or replaced:** MinIO (OSS repository archived, no community images; replaced by SeaweedFS). `exceljs` (no release since 2024; Excel parsing moves server-side to ClosedXML, which also suits the server-side validation of US-P1-ORG-03).

## 7.2 Why a modular monolith (not microservices)

- A team of 5–7 cannot operate 10+ services well; distributed transactions would complicate the **signed, append-only** guarantees.
- Bounded contexts are enforced by **project references + architecture tests** (`tests/Karry.ArchitectureTests`, on the project graph; type-level rules from P1.5), and communicate via **domain events** through the outbox — so extraction later is mechanical.
- Expected first extractions (only if metrics justify): **Telemetry Ingestion** (high write volume), **AI Gateway** (independent scaling / provider keys), **Analytics sidecar** (already separate, Python).

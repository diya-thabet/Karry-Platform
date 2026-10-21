---
title: "Technology Stack"
area: architecture
tags: [stack, dotnet, react, postgresql, keycloak, python]
source: "MASTER_PLAN.md §7"
status: baseline
last_updated: 2026-10-21
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
| **Messaging (in-process)** | Domain events + **transactional outbox** in PostgreSQL; Wolverine *or* lightweight in-house dispatcher | Reliable without extra infra in MVP. ⚠ MediatR, AutoMapper, MassTransit v9 moved to commercial licences in 2025 — avoid or budget | RabbitMQ / Kafka later if needed |
| **Background jobs** | Hangfire (PostgreSQL storage) or Quartz.NET | RUL recompute, alerts, report generation, chain anchoring | — |
| **Validation** | FluentValidation | Expressive server-side rules | DataAnnotations |
| **PDF / documents** | QuestPDF (check licence tier vs. revenue) | Scale tickets, waybills, shift reports | — |
| **Analytics/ML sidecar** | Python 3.13, FastAPI, NumPy, SciPy, pandas/Polars, statsmodels, scikit-learn, lifelines (Weibull) | Best ecosystem for forecasting & anomaly detection | ML.NET |
| **Database** | **PostgreSQL 17/18** | Reliability, RLS, JSONB, extensions | SQL Server |
| **Extensions** | PostGIS (spatial), TimescaleDB (telemetry hypertables, continuous aggregates), pgcrypto, pg_partman (if needed) | Spatial blast/stockpile, efficient time-series | Separate TSDB (InfluxDB) |
| **Cache** | Redis / Valkey | Sessions-free API, rate-limit counters, hot dashboard caches | — |
| **Object storage** | S3-compatible (AWS S3 / Cloudflare R2 / MinIO on-prem) with Object Lock | Photos, signatures, PDFs, WORM audit anchors | — |
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
| **Quality** | xUnit, FsCheck (property-based), Testcontainers, Playwright, Vitest, k6, Stryker.NET (mutation on math) | — | — |
| **Security tooling** | GitHub Advanced Security / CodeQL, Trivy, OWASP ZAP, gitleaks, Dependabot/Renovate | — | Snyk |

> Exact library versions are pinned in Phase 0 (`docs/adr/0002-version-pinning.md`) and upgraded monthly via Renovate.

## 7.2 Why a modular monolith (not microservices)

- A team of 5–7 cannot operate 10+ services well; distributed transactions would complicate the **signed, append-only** guarantees.
- Bounded contexts are enforced by **project references + ArchUnitNET tests**, and communicate via **domain events** through the outbox — so extraction later is mechanical.
- Expected first extractions (only if metrics justify): **Telemetry Ingestion** (high write volume), **AI Gateway** (independent scaling / provider keys), **Analytics sidecar** (already separate, Python).

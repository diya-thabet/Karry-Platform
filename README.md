# Karry Platform

*Application pour la Gestion de Carrière*: multi-tenant SaaS for quarries, crushing plants, sand washing and open-pit mines. Trusted field data (offline-first, signed), predictive maintenance, inventory, plant mass balance, per-machine finance and an AI copilot.

- **Architecture:** modular monolith (.NET 10, PostgreSQL + PostGIS/TimescaleDB/pgvector, React PWA). See [docs/02-architecture](docs/02-architecture/README.md) and the [ADRs](docs/adr/README.md).
- **Plan and status:** [phases](docs/04-phases/README.md); current phase: [Phase 1 sub-phases](docs/04-phases/phase-01-platform-foundation/sub-phases.md) and [tracker](docs/04-phases/phase-01-platform-foundation/tracker.md).
- **How we work** (tests, CI, security, git): [docs/00-llm/llm_guidelines.md](docs/00-llm/llm_guidelines.md). Docs map: [docs/README.md](docs/README.md).

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | 10.0.402 (see `global.json`) | |
| Node.js | 24 LTS (see `.nvmrc`) | `nvm install` |
| pnpm | 12 (see `src/frontend/package.json`) | `corepack enable` |
| Docker + Compose | Docker 27+, Compose v2 | Your user must be in the `docker` group |

## Run everything locally (≤ 10 commands)

```bash
docker compose -f deploy/docker/compose.yaml up -d --wait    # 1. PostgreSQL, Keycloak, S3 (SeaweedFS), Valkey, Mailpit, Grafana LGTM
dotnet build Karry.slnx                                        # 2. build the backend
dotnet test --solution Karry.slnx                              # 3. backend tests (Testcontainers needs Docker)
dotnet run --project src/backend/Karry.Host -- migrate         # 4. apply database migrations (as karry_owner)
dotnet run --project src/backend/Karry.Host -- seed-demo       #    optional: demo tenants demo-carriere-a / -b
dotnet run --project src/backend/Karry.Host                    # 5. API on http://localhost:5000 (/health/ready)
cd src/frontend && corepack enable && pnpm install             # 6. frontend dependencies
pnpm --filter @karry/portal dev                                # 7. office portal (Vite dev server)
pnpm --filter @karry/field dev                                 # 8. field app (Vite dev server)
```

Database roles come from [`deploy/database/roles.sql`](deploy/database/roles.sql) on the first start of the stack. A stack created before P1.5 lacks `karry_owner`: recreate it once with `docker compose -f deploy/docker/compose.yaml down -v`.

Local services (bound to `127.0.0.1` only; development credentials are in [`deploy/docker/compose.yaml`](deploy/docker/compose.yaml)):

| Service | URL / port |
|---|---|
| PostgreSQL 17 (`karry` database; `karry_app` runtime role, `karry_owner` migrations role) | `localhost:5432` |
| Keycloak admin console | http://localhost:8080 |
| S3 API (SeaweedFS, Object Lock) | http://localhost:8333 |
| Valkey (Redis-compatible) | `localhost:6379` |
| Mailpit (all local e-mail) | http://localhost:8025 |
| Grafana (traces, metrics, logs, Karry dashboards and alerts) | http://localhost:3000 |

Stop with `docker compose -f deploy/docker/compose.yaml down` (add `-v` to delete the data).

## Checks run by CI

```bash
deploy/docker/smoke-test.sh          # TC-P1-PLT-004: every local service works, incl. S3 Object Lock
tools/templates/test-template.sh     # TC-P1-PLT-003: the module template produces a valid module
cd src/frontend && pnpm lint && pnpm typecheck && pnpm test && pnpm build
python3 deploy/observability/dashboards/generate.py --check   # dashboards as code are up to date
python3 tools/docs/check_links.py    # docs: 0 broken links
```

## Repository layout

`src/backend` (.NET host, workers, kernel, modules) · `src/frontend` (portal, field app, packages) · `tests` (architecture and cross-cutting tests) · `deploy` (local stack, later IaC) · `tools` (module template, docs checker) · `spikes` (frozen Phase 0 evidence) · `docs`. Details: [repository structure](docs/02-architecture/repository-structure.md). New module: `tools/new-module.sh <Name>`.

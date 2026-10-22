# Phase 0 Technical Spikes

Throwaway-quality **code**, production-quality **evidence**. Each spike answers one risky technical question from Phase 0 ([phase plan](../docs/04-phases/phase-00-discovery/plan.md)) with automated tests that run in GitHub Actions ([`spikes.yml`](../.github/workflows/spikes.yml)). Plain-English overview: [Technical Spikes Explained](../docs/04-phases/phase-00-discovery/spikes-explained.md). Spike code is **not** promoted into `src/` as-is; Phase 1 re-implements the proven patterns properly.

| Work package | Spike | Question | Result |
|---|---|---|---|
| WP-P0-12 | [rls-pooling](rls-pooling/README.md) | Does PostgreSQL RLS + Npgsql pooling isolate tenants with acceptable overhead? | ✅ Isolation proven (0 leaks / 10,000 concurrent ops). Policy cost +2.4–4.0 %. ⚠ Extra round trip +14 % on 1-query transactions: batch `set_config` in Phase 1 |
| WP-P0-11 | [field-capture-pwa](field-capture-pwa/README.md) + [signature-interop](signature-interop/README.md) | Can a non-extractable WebCrypto key persist, sign offline, and be verified by the .NET server? | ✅ Survives 10 restarts, non-exportable, verifies in Node and .NET. ⚠ Lost on "clear site data", so re-enrollment flow required |
| WP-P0-10 | [field-capture-pwa](field-capture-pwa/README.md) | Does the offline outbox (Dexie + service worker) keep data across reloads/restarts and sync exactly once? | ✅ in desktop Chromium (1,000 entries + 50 × 200 KB photos). ⏳ Real 2 GB Android tablet, 72 h run: manual device-lab step |
| WP-P0-13 | [pg-extensions](pg-extensions/README.md) | Are PostGIS + TimescaleDB + pgvector available together on PostgreSQL 17 and 18? | ✅ PG 17.11 and 18.6: PostGIS 3.6.4, TimescaleDB 2.31.0, pgvector 0.8.7, pgcrypto (CI, 4 consecutive green runs). ⏳ Managed-provider check depends on hosting decision (WP-P0-14) |

## Run locally

```bash
# .NET spikes (needs a PostgreSQL superuser connection; any PG ≥ 16)
cd spikes
export KARRY_SPIKE_PG="Host=127.0.0.1;Port=5432;Username=postgres;Password=postgres"
dotnet test --solution Karry.Spikes.slnx -c Release

# PWA spike
cd spikes/field-capture-pwa
pnpm install --frozen-lockfile && pnpm exec playwright install chromium
pnpm typecheck && pnpm test

# Extension check (needs Docker)
spikes/pg-extensions/check-extensions.sh timescale/timescaledb-ha:pg17
```

Test case IDs (`TC-P0-…`) are listed in the [Phase 0 tracker](../docs/04-phases/phase-00-discovery/tracker.md).

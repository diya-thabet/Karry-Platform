# Spike WP-P0-12 — Row-Level Security with connection pooling

**Question.** Can PostgreSQL Row-Level Security, driven by a per-transaction tenant setting, isolate tenants safely when connections are pooled by Npgsql, with acceptable latency overhead (target < 5 %)?

**Status:** ✅ Answered, with one design change for Phase 1. Measured 2026-10-21 (PostgreSQL 17.5, Npgsql 10.0.3, .NET 10.0.402, 12-core Linux workstation, local TCP).

## Design under test

- Tables owned by `karry_spike_owner`; the app connects as `karry_spike_app` (not owner, `NOBYPASSRLS`, SELECT/INSERT only).
- `ENABLE` + `FORCE ROW LEVEL SECURITY`; policy with `USING` and `WITH CHECK`:
  `tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid`
- [`TenantScope`](src/Karry.Spikes.RlsPooling/TenantScope.cs) opens a pooled connection, begins a transaction and runs `SELECT set_config('app.tenant_id', $1, true)`, the **parameterized** equivalent of `SET LOCAL`.

## Results: tests ([TenantIsolationTests.cs](tests/Karry.Spikes.RlsPooling.Tests/TenantIsolationTests.cs))

| Test | Verifies | Result |
|---|---|---|
| TC-P0-RLS-001 | Tenant sees only its own rows | ✅ |
| TC-P0-RLS-002 | Insert for another tenant rejected (`42501`) | ✅ |
| TC-P0-RLS-003 | No tenant context → 0 rows, insert rejected (fail closed) | ✅ |
| TC-P0-RLS-004 | Reused session without context → 0 rows **without error** (with and without connection reset) | ✅ |
| TC-P0-RLS-005 | *Hazard:* session-level `set_config(..., false)` leaks to the next pool user when reset is disabled | ✅ leak reproduced |
| TC-P0-RLS-006 | Transaction-local setting never leaks, even with reset disabled | ✅ |
| TC-P0-RLS-007 | App role is not owner and has no `BYPASSRLS` | ✅ |
| TC-P0-RLS-008 | Owner filtered only when RLS is `FORCE`d | ✅ |
| TC-P0-RLS-009 | 10,000 concurrent ops, 50 tenants, pool 20, 64 parallel → **0 cross-tenant rows**, exact per-tenant counts | ✅ |
| TC-P0-RLS-010 | Empty tenant id refused before any DB call | ✅ |

**Mutation check** (each bug injected, then reverted): policy without `nullif` → 4 tests fail; RLS not forced → 1 fails; session-level `set_config` → 2 fail.

## Results: overhead ([bench](bench/Karry.Spikes.RlsPooling.Bench/Program.cs))

20,000 single-query transactions, 32 parallel, 100,000 rows, 3 runs (mean latency vs A):

| Variant | Run 1 | Run 2 | Run 3 |
|---|---|---|---|
| A: no RLS, explicit `WHERE tenant_id` | 0 % | 0 % | 0 % |
| B: no RLS + separate `set_config` round trip | +14.8 % | +13.7 % | +14.7 % |
| C: RLS + `TenantScope` (2 round trips) | +19.3 % | +16.4 % | +19.3 % |
| D: RLS + `set_config` batched with the query (1 round trip) | +5.2 % | +5.2 % | +12.0 % |
| **Policy evaluation alone (C vs B)** | **+3.9 %** | **+2.4 %** | **+4.0 %** |

## Findings & decisions for Phase 1

1. **RLS itself meets the < 5 % target** (+2.4–4.0 %). The cost in C comes from the **extra round trip** for `set_config` (~0.45 ms per transaction on local TCP, more over a real network).
   → Phase 1 `TenantScope` must send `set_config` **in the same batch** as the first statement (`NpgsqlBatch`), or prepend it to the transaction's first command. Requests with several statements amortize it further.
2. **The documented policy was wrong.** `current_setting('app.tenant_id', true)::uuid` throws `invalid input syntax for type uuid: ""` on a session that previously used a transaction-local setting. **Use `nullif(current_setting('app.tenant_id', true), '')::uuid`.** Architecture docs and guidelines are updated accordingly.
3. **Never use session-level `SET`/`set_config(..., false)`** for tenant context: TC-P0-RLS-005 proves the leak with connection reset disabled (also the default behaviour behind PgBouncer transaction pooling).
4. `FORCE ROW LEVEL SECURITY` is required whenever the app role could own tables (TC-P0-RLS-008). Keep migrations under a separate owner role.
5. Prefer `set_config($1, …, true)` over string-built `SET LOCAL`, since only the former can be parameterized.

## Remaining risks

- Overhead measured locally; repeat on the chosen managed PostgreSQL with real network latency (WP-P0-13/14).
- EF Core integration (interceptor + global query filters) not covered here; it is US-P1-TEN-02.

---
title: "ADR-0004: Multi-tenancy: pooled database with Row-Level Security, silo option"
area: adr
tags: [tenancy, rls, security, saas]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0004 — Multi-tenancy: pooled database with Row-Level Security, silo option

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** WP-P0-12; spike [rls-pooling](../../spikes/rls-pooling/README.md); US-P1-TEN-02/03

## Context

Karry is a SaaS sold to many companies. Tenant data must never leak, cost per tenant must stay low for Starter/Professional plans, and Enterprise customers may require dedicated infrastructure or data residency.

## Options considered

1. Database per tenant for everyone — **Pros:** Strong isolation. **Cons:** High cost and operational load at hundreds of tenants; slow provisioning.
2. Shared tables, application filtering only — **Pros:** Cheap. **Cons:** One missing `WHERE` leaks data.
3. **Pooled tables + PostgreSQL RLS + app filters; silo option** (chosen) — **Pros:** Defense in depth at low cost; instant provisioning; Enterprise can still get a dedicated DB/cell. **Cons:** Requires strict conventions (proven workable by the spike).

## Decision

Every tenant table carries `tenant_id uuid not null`, with RLS `ENABLE` + `FORCE` and a policy on `nullif(current_setting('app.tenant_id', true), '')::uuid` for both `USING` and `WITH CHECK`. The application role is not the table owner and has no `BYPASSRLS`; migrations run as a separate owner role. Each request runs in a transaction that sets the tenant with `set_config('app.tenant_id', $1, true)` (transaction-local, parameterized), **batched with the first statement**. EF Core global query filters are the second layer, and an automated cross-tenant test suite covers every endpoint. Enterprise tenants can be placed on a dedicated database or cell with the same code.

## Consequences

- ✅ Spike evidence: 0 cross-tenant rows in 10,000 concurrent mixed-tenant operations; RLS policy cost +2.4–4.0 %.
- ⚠ A separate `set_config` round trip costs ~+14 % on single-query transactions, so batching is mandatory.
- ⚠ Session-level `SET` is forbidden (spike reproduced a leak with connection reset disabled).
- 🔎 Revisit for tenants whose size justifies their own cell.

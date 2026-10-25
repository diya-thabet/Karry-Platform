---
title: "ADR-0019: Feature flags with OpenFeature and a database-backed provider"
area: adr
tags: [feature-flags, openfeature, entitlements, audit]
source: "Phase 1 sub-phase P1.0"
status: accepted
last_updated: 2026-10-25
---

# ADR-0019 — Feature flags with OpenFeature and a database-backed provider

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-22
- **Deciders:** Product owner, software architect
- **Related:** US-P1-PLT-05, US-P1-BIL-03, ADR-0008

## Context

US-P1-PLT-05 needs flags evaluable per tenant, user and environment, changeable without redeploy, with audited changes. Flags are **not** entitlements: entitlements say what a customer has paid for (Billing module); flags control rollout of code (incomplete features, canaries, kill switches).

## Options considered

1. **OpenFeature SDK + in-house provider backed by PostgreSQL** (chosen): vendor-neutral API in .NET and React; flags stored per tenant; every change is a ledger entry. **Pros:** no extra service, audit for free, tenant-aware. **Cons:** we build a small admin UI and cache.
2. flagd / Unleash / GrowthBook self-hosted: **Cons:** another service to run and secure; audit into our ledger would need integration.
3. SaaS (LaunchDarkly, etc.): **Cons:** cost, tenant data leaves our boundary.

## Decision

Use the **OpenFeature** SDKs (.NET and web) with a Karry provider reading flags from the database (cached ≤ 60 s, invalidated on change). Flag changes are commands that write a ledger entry. A different provider (flagd, Unleash) can be plugged in later without touching call sites.

## Consequences

- ✅ Standard API at call sites; no new infrastructure.
- ✅ Flag changes are auditable like any other change (TC-P1-PLT-009).
- ⚠ Flags must not be used for plan gating; that is `RequiresFeature` / entitlements (US-P1-BIL-03).

## Implementation (P1.10 slice 6)

- **Definitions in code.** Every flag is a `FlagDefinition` (key, description, default) in `Karry.SharedKernel.Flags.FeatureFlags`. A value for an unknown key is refused (`flags.flag_unknown`), so a typo cannot create a flag nobody reads. The first flag is `portal.usage-banner`, the kill switch of the portal's over-limit banner.
- **Evaluation order:** user override → company override → platform-wide value → environment (`Flags:Environment:<key>` in configuration) → code default. A `null` value at any level means "not set here". The `Flags` module does the evaluation (`FlagRules.Evaluate`), caches it 60 s per tenant and forgets the cache when a value changes (a tenant change forgets that tenant; a platform-wide change forgets everything), so a change takes effect at once on that instance and within 60 s everywhere.
- **Audit.** Company and user overrides (`flags.tenant_flags`, RLS) are changed in the addressed tenant's unit of work; each change raises `flags.tenant_flag_changed.v1` and lands in that tenant's ledger chain (TC-P1-PLT-009). Platform-wide values (`flags.platform_flags`, a global table) keep their history in `flags.platform_flag_changes`, which is append-only (database trigger, `UPDATE`/`DELETE`/`TRUNCATE` revoked from `karry_app`). **Follow-up:** platform-wide changes (plans, platform flags) have no ledger chain of their own yet; one platform chain is planned with the operator console.
- **.NET.** `KarryFlagProvider` is an OpenFeature `FeatureProvider` (boolean flags only; other types answer `TYPE_MISMATCH`, unknown keys `FLAG_NOT_FOUND`). The OpenFeature API is a process singleton, so each host sets its provider on its own OpenFeature domain (`FeatureFlagDomain`); call sites use the scoped `FeatureFlagClient`, which passes the tenant, the user (token subject, as targeting key) and the environment as evaluation context.
- **Web.** The portal uses `@openfeature/web-sdk` and `@openfeature/react-sdk` with a provider that loads `GET /api/v1/flags` (the caller's evaluated values) on start and on a context change; until it has loaded, and for a key the API does not return, the caller's default applies. Components read flags with `useBooleanFlagValue`. Display only: the API checks every call.
- **API.** `GET /api/v1/flags` (any member); platform operators set values with `PUT /api/v1/flags/{key}` and `PUT /api/v1/flags/tenants/{tenantId}/{key}` ([API standards](../02-architecture/api-standards.md)). An operator screen comes with the operator console.

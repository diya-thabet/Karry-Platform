---
title: "ADR-0019: Feature flags with OpenFeature and a database-backed provider"
area: adr
tags: [feature-flags, openfeature, entitlements, audit]
source: "Phase 1 sub-phase P1.0"
status: accepted
last_updated: 2026-10-22
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

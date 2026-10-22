---
title: "ADR-0002: Backend on .NET 10 LTS with version pinning and upgrade policy"
area: adr
tags: [dotnet, runtime, versions, lts]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0002 — Backend on .NET 10 LTS with version pinning and upgrade policy

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** Spikes [rls-pooling](../../spikes/rls-pooling/README.md), [signature-interop](../../spikes/signature-interop/README.md)

## Context

The original specification named .NET 9, a Standard-Term Support release whose support ends in November 2026, before Karry's first commercial release. A multi-year SaaS needs a supported runtime and a predictable upgrade path.

## Options considered

1. .NET 9 (STS) — **Pros:** Matches the original spec. **Cons:** End of support Nov 2026; immediate forced upgrade.
2. **.NET 10 (LTS)** (chosen) — **Pros:** Supported to Nov 2028; current C# and ASP.NET Core; verified working in Phase 0 spikes (SDK 10.0.402). **Cons:** None significant.
3. Node.js / Java — **Pros:** Large ecosystems. **Cons:** Team skills and existing plan target .NET; no benefit for this domain.

## Decision

Use .NET 10 LTS for all backend code. Pin the SDK in `global.json` (`rollForward: latestFeature`), lock NuGet dependencies (`RestorePackagesWithLockFile`, `--locked-mode` in CI), enable `Nullable` and `TreatWarningsAsErrors`. Use the Microsoft Testing Platform runner (`global.json` → `test.runner`), required for `dotnet test` with xunit v3 on the .NET 10 SDK (found in spike). Apply patch updates monthly via Renovate; plan the move to the next LTS (.NET 12, expected Nov 2027) within 6 months of its release.

## Consequences

- ✅ Support window covers the first two years of commercial operation.
- ⚠ Library licence changes (MediatR, AutoMapper, MassTransit v9, FluentAssertions v8) must be checked when adding packages (guidelines §18).
- 🔎 Revisit at each LTS release.

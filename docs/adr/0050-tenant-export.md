---
title: "ADR-0050: Tenant export"
area: adr
tags: [adr, phase-3, p3-15, tenancy, export, data-portability, privacy]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.15), US-P3-OPS-04"
status: proposed
last_updated: 2026-11-27
---

# ADR-0050: Tenant export

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

A customer may leave, or audit us, at any time; their data must come out complete and readable.

## Decision

### 1. An asynchronous job

`tenancy.export_job`: one CSV per entity (UTF-8, documented columns) from every module through an `IExportable`
contract, plus a manifest of documents and evidence with their hashes, zipped into the tenant's storage prefix; ready
within an hour for a pilot-sized tenant.

### 2. Access

Requested by `tenancy.export.request` (Tenant Admin), ledgered; downloaded by a signed link valid 7 days, bound to the
tenant; sensitive fields follow the requester's masking.

## Consequences

- Data portability is a feature, not a support ticket. Every module now owes an `IExportable`.

## Alternatives considered

| Option | Why not |
|---|---|
| Database dump | Not readable by a customer; leaks other tenants' structure |

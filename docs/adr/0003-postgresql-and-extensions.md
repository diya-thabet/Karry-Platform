---
title: "ADR-0003: PostgreSQL 17/18 with PostGIS, TimescaleDB and pgvector"
area: adr
tags: [postgresql, postgis, timescaledb, pgvector, database]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0003 — PostgreSQL 17/18 with PostGIS, TimescaleDB and pgvector

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** WP-P0-13; spike [pg-extensions](../../spikes/pg-extensions/README.md)

## Context

Karry needs relational integrity and RLS (tenancy), spatial data (sites, benches, blast polygons, geofences), high-volume time series (telemetry, flows, tank levels) and vector search (copilot document retrieval). Running separate databases for each need would multiply operations work for a small team.

## Options considered

1. **PostgreSQL + extensions** (chosen) — **Pros:** One engine, one backup/HA story, RLS everywhere, mature extensions. **Cons:** Depends on extension availability on the managed provider.
2. PostgreSQL + separate TSDB (InfluxDB) + vector DB — **Pros:** Specialised engines. **Cons:** More infra, cross-store consistency, more cost.
3. SQL Server — **Pros:** Mature. **Cons:** Licensing cost; weaker spatial/vector story for our needs.

## Decision

Use PostgreSQL as the single primary datastore with PostGIS, TimescaleDB, pgvector and pgcrypto. The major version (17 or 18) is the highest version for which the CI extension check passes **and** the chosen managed provider supports all four extensions. Dev/CI use the `timescale/timescaledb-ha` image matching that major version. If the provider lacks TimescaleDB, telemetry falls back to native partitioning (`pg_partman`) and this ADR is amended.

## Consequences

- ✅ One operational surface; RLS applies uniformly.
- ⚠ Provider choice (WP-P0-14) constrains extensions, so this ADR stays **Proposed** until hosting is decided.
- 🔎 Revisit if telemetry ingestion exceeds what a single cluster handles (see ADR-0001 extraction triggers).

---
title: "ADR-0024: Sites as PostGIS geometry, shift time computed with NodaTime in the site's time zone"
area: adr
tags: [organization, sites, postgis, geometry, shifts, calendars, time-zones, dst, nodatime, maps]
source: "Phase 1 sub-phase P1.11"
status: accepted
last_updated: 2026-10-26
---

# ADR-0024 — Sites as PostGIS geometry, shift time computed with NodaTime in the site's time zone

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner pre-approved and the agent merged the green P1.11 PRs #66–#71; built and verified 2026-10-26)
- **Date:** 2026-10-26
- **Deciders:** Product owner, software architect
- **Related:** US-P1-ORG-01, US-P1-ORG-02, US-P1-TEN-04; [ADR-0004](0004-multi-tenancy-rls.md) (RLS),
  [ADR-0022](0022-authorization-engine.md) (site scopes), [ADR-0023](0023-plans-subscriptions-entitlements.md)
  (usage meters); [technology stack](../02-architecture/technology-stack.md) (PostGIS, MapLibre)

## Context

A site is where everything happens: shift entries, scale tickets, fuel, stock and machines all belong to one. Later
phases need the site's **location** (weather engine, Phase 7), its **boundary** (geofencing of machines and stockpiles)
and its **shift instances** (every field entry is attached to a shift; the field app works offline for days). Quarries
run night shifts that cross midnight, in countries that may change clocks twice a year, so the hard part is time:
a shift "18:00–06:00" on the night clocks go back lasts 13 real hours, and on the night they go forward 11. Getting
this wrong silently shifts production, fuel and labour figures from one day to another.

## Options considered

1. **Plain columns and `DateTime` arithmetic** (latitude/longitude as numbers, polygons as JSON, shifts as UTC
   offsets). Pros: no new library. Cons: no spatial queries later, polygons not validated by the database, and DST
   handled by hand in each module.
2. **PostGIS geometry with NetTopologySuite, NodaTime for wall-clock time** (chosen). PostGIS is already in the
   database image (WP-P0-13); NetTopologySuite is the .NET geometry library the Npgsql provider maps natively;
   NodaTime separates local dates and times from instants and resolves DST gaps and overlaps explicitly.
3. **A geography type and `TimeZoneInfo`.** `geography` costs more for no gain at quarry scale (a few km), and
   `TimeZoneInfo` depends on the host's time-zone data (container images differ), while NodaTime ships its own IANA
   database, so results do not change with the host.

## Decision

### Module

A new **`Organization`** module owns sites, shift patterns, calendars and shift instances. Company settings stay in
`Tenancy` (they are read before a tenant is resolved, for the sign-in page); a site's time zone defaults to the
company's.

### Sites and geometry

- `organization.sites` (RLS): code (unique per company), name, type (`quarry`, `sand-washing`, `mine`,
  `warehouse-only`, `workshop`), address (line, city, region, postal code, ISO 3166-1 country), **location** as
  `geometry(Point, 4326)` (required: the weather engine needs it), optional **boundary** as `geometry(Polygon, 4326)`,
  IANA time zone, optional currency override, status `active` / `archived`.
- Mapped with **NetTopologySuite** through `Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite` (10.0.3).
- **Validated in the domain**: coordinates in range, a closed ring of at least four points (first equals last), valid
  and simple (no self-intersection, NTS `IsValid`), at most 500 points, an area of at most 200 km². The database checks
  `ST_IsValid` again.
- **On the wire**: `location` is `{ latitude, longitude }` (readable, no axis-order trap); `boundary` is a GeoJSON
  Polygon (RFC 7946, `[longitude, latitude]`). Plain DTOs, no extra serializer package.
- **PostGIS is created by the environment**, like the other extensions (`deploy/docker/postgres/init`); a migration
  declares `CREATE EXTENSION IF NOT EXISTS postgis`, a no-op where it exists and a clear failure where it does not.
- Sites are **archived, never deleted**: an archived site keeps its history, leaves the default lists and the meter,
  and can be restored (a restore is a creation for the meter).
- A site is `ISiteScoped` by its own ID: a site role sees and manages only its sites (ADR-0022).

### Usage meter

The `sites` metric counts active sites. Its limit is the number of sites **bought** in the subscription
(`TenantEntitlements.Sites`), under the same soft-limit rule as other metrics (limit + 10 % grace, at least one).
This amends ADR-0023, where sites were bought but not limited.

### Shift time

- **NodaTime** (3.3.5) for every wall-clock rule, in `Organization.Domain`.
- **Shifts are wall-clock.** A shift definition has a local start and a local end time; when the end is not after the
  start, it ends the next day. A shift "18:00–06:00" ends at 06:00 on the site's clock whatever the date, so its real
  length is 11, 12 or 13 hours around DST changes. Durations used for labour, hour meters and rates always come from
  the instants, never from the definition.
- **DST resolution:**
  - A local time inside a spring-forward gap moves forward by the gap (02:30 becomes 03:30).
  - An ambiguous local time in a fall-back overlap takes the earlier offset, so the shift starts at the first 01:30.
  - Stored instances keep the start and end as UTC instants, the local date, and the time zone used.
- **An instance belongs to the date it starts on.** A night shift starting on 31 December is a 31 December shift.
- **Effective-dated patterns.** A site's pattern has versions with `effectiveFrom` (a local date). The pattern for a
  date is the newest version effective on or before it; a change never rewrites past instances.
- A pattern lists its shifts (code, name, start, end) and the weekdays they run on. Two shifts of a version never run
  at the same time on the wall clock (checked over the week, across midnight and from Sunday to Monday), so a field
  entry belongs to exactly one shift.
- **Only the future changes.** A version starts after the site's today (the site's first version may start on any
  date); a version in effect is never changed or removed. Non-working days change from the site's today on.
- **Calendars:** non-working days per site (date, kind `public-holiday` / `closure` / `other`, name). A non-working
  day has no instances.
- **Generation:**
  - A daily Quartz job (with the start-up trigger of P1.10) writes the next 30 days into
    `organization.shift_instances` (RLS, idempotent by site, date and shift code). It removes future instances a
    change made invalid; it never touches one that has started. A pattern, non-working day or site change (time
    zone, archive, restore) applies the same rule to its site in the same transaction, so the stored days are right
    at once; the job moves the window forward and catches up after downtime (`Karry.Workers generate-shifts`, alert
    `KarryShiftInstancesStale`).
  - `GET /sites/{id}/shift-instances?from&to` computes any range of up to 366 days from the rules, without storing it
    (by default the next 30 days).
  - The field app (P1.15) syncs the stored instances.

### Map

The portal map picker uses **MapLibre GL JS** (technology stack) with a configurable style URL (`VITE_MAP_STYLE_URL`).
The tile provider is an owner decision (hosting, cost); until then the default is MapLibre's public demo style for
development only. Numeric latitude and longitude fields are always available: they are the accessible path (keyboard,
screen readers) and the offline one. The map code is a separate chunk loaded only when a site page opens; where WebGL
is missing the page says so and keeps the fields.

## As built (P1.11 close-out)

- **Geometry:** pin `geometry(Point, 4326)`, optional boundary `geometry(Polygon, 4326)`: one closed, simple ring of 4–500
  points, at most 200 km². The application checks it with NetTopologySuite and the database checks `ST_IsValid` again.
  PostGIS is created by the environment (`deploy/database/extensions.sql`), never by a migration.
- **Shift time:** pattern versions with an effective date; only dates after the site's today change, except a site's
  first pattern. The 30 coming days of instances are stored in the same transaction as each change and refreshed by a
  daily job (alert `KarryShiftInstancesStale`). Any range up to 366 days is computed on request. The NodaTime resolver
  moves a start in a spring gap forward and takes the earlier offset in an autumn overlap.
- **Tests:** property tests over random zones, dates, patterns and holidays (TC-P1-ORG-003). The isolation suite attacks
  sites, patterns and days of another company. 26 mutation checks were caught across the sub-phase.
- **Test servers:** TimescaleDB background workers are off in test databases (`timescaledb.max_background_workers=0`).
  Its scheduler kept sessions open on the template database (#69).
- **Portal:** the map is a lazy chunk; the latitude and longitude fields remain the accessible and offline path.

## Consequences

- ✅ Spatial queries (inside a boundary, distance to a site) are ready for geofencing and weather.
- ✅ Shift instances are right across DST changes and midnight. Property tests over random dates, zones and patterns
  prove it (TC-P1-ORG-003).
- ✅ A pattern or holiday change takes effect from its date, with no rewritten history.
- ⚠ Two new packages: NodaTime, and the Npgsql NetTopologySuite plugin with NetTopologySuite.
- ⚠ Real shift length varies around DST changes, so reports must compute durations from instants. A test in each
  module that reports hours guards this.
- ⚠ **Owner input:** the map tile provider (MapTiler, Stadia or self-hosted) before production.
- ⚠ **Owner input:** typical shift patterns and holiday calendars (open question 7). The demo uses 2×12 h and
  3×8 h until then.

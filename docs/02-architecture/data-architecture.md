---
title: "Data Architecture"
area: architecture
tags: [database, schema, timescaledb, postgis, retention]
source: "MASTER_PLAN.md §11"
status: baseline
last_updated: 2026-10-21
---

# Data Architecture

> [Docs index](../README.md) › [02-architecture](README.md)

## 11.1 Storage layout

| Store | Content |
|---|---|
| PostgreSQL (OLTP) | One database per region; **schema per module**; all tenant tables carry `tenant_id` + RLS |
| TimescaleDB hypertables | `telemetry.machine_signal`, `production.flow_sample`, `fuel.tank_level` — continuous aggregates (1 min, 1 h, 1 day) |
| PostGIS | Site boundaries, benches, blast patterns, stockpile polygons, GPS of captures |
| pgvector | Embeddings of documents/manuals/SOPs for the copilot (tenant-scoped) |
| Object storage | Photos (meter evidence), signature images, PDFs, exports, ledger anchors (Object Lock, compliance mode) |
| Redis | Rate limits, short-lived caches, distributed locks |

## 11.2 Key conventions

- **Primary keys:** UUIDv7 (client-generatable offline, index-friendly).
- **Timestamps:** `timestamptz` UTC everywhere; site timezone stored on `Site`; shifts computed in site local time.
- **Soft delete:** *never* for signed/ledger data. Master data uses `archived_at`.
- **Optimistic concurrency:** `xmin`-based concurrency tokens on mutable aggregates.
- **Money:** `numeric(19,4)` + `char(3)` currency.
- **Quantities:** `numeric(18,6)` + unit code (+ material state where relevant).
- **Migrations:** EF Core migrations reviewed as SQL; expand/contract pattern for zero-downtime.

## 11.3 Illustrative core tables (abridged)

The shift entry as built is in [ADR-0034 §3, §8](../adr/0034-shift-entries.md) (append-only versions, `numeric(9,1)` meter values); the sketch below is the original idea.

```sql
-- fieldops.shift_entry
id uuid pk, tenant_id uuid, site_id uuid, asset_id uuid, operator_id uuid,
shift_id uuid, meter_type text, start_value numeric, end_value numeric,
start_photo_key text, end_photo_key text, fuel_gauge_pct numeric,
device_time timestamptz, received_at timestamptz, geo geography(point),
status text check (status in ('PENDING','APPROVED','REJECTED','CORRECTED','SUPERSEDED')),
supersedes_id uuid null, signature_envelope_id uuid, payload_hash bytea

-- audit.ledger_entry   (append-only, hash-chained per tenant)
seq bigint, tenant_id uuid, occurred_at timestamptz, actor_id uuid, device_id uuid,
aggregate_type text, aggregate_id uuid, event_type text,
payload jsonb, payload_hash bytea, prev_hash bytea, entry_hash bytea,
primary key (tenant_id, seq)

-- weighbridge.scale_ticket   (no UPDATE allowed; reversals reference original)
id uuid, tenant_id uuid, site_id uuid, ticket_no text, truck_plate text,
gross_kg numeric, tare_kg numeric, net_kg numeric generated always as (gross_kg - tare_kg) stored,
product_id uuid, customer_id uuid, order_id uuid, weighmaster_id uuid,
source text check (source in ('INDICATOR','MANUAL')), reverses_ticket_id uuid null,
signature_envelope_id uuid, issued_at timestamptz
```

## 11.4 Read models & analytics

- CQRS-lite: commands write normalised tables; **projections** (updated by outbox consumers) feed dashboards.
- Heavy aggregates via TimescaleDB continuous aggregates and materialised views refreshed incrementally.
- Data warehouse (later, Phase 9+): nightly CDC to a columnar store (e.g., ClickHouse or DuckDB/Parquet in object storage) for cross-tenant **anonymised** benchmarking — opt-in only.

## 11.5 Retention

| Data | Default retention |
|---|---|
| Ledger, signed records, tickets, waybills | ≥ 10 years (configurable to local legal requirements) |
| Photos | 3 years (configurable), hash kept forever in ledger |
| Raw telemetry | 13 months raw, aggregates forever |
| AI conversation logs | 12 months |
| Application logs | 30 days hot, 1 year cold |

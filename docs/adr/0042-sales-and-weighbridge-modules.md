---
title: "ADR-0042: Sales and weighbridge modules"
area: adr
tags: [adr, phase-3, p3-1, sales, weighbridge, customers, products, price-lists, trucks]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.1), US-P3-SAL-01, SAL-02, SAL-04"
status: proposed
last_updated: 2026-11-27
---

# ADR-0042: Sales and weighbridge modules

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

The bounded contexts draw one "Weighbridge & Sales/Dispatch" context. Phase 3 needs customers, products and price
lists for the weighbridge, and the same data will be read by invoicing (P7) and a customer portal later. Scale tickets
are signed field evidence with gap-free numbering.

## Decision

### 1. Two modules

- **`Sales`** (schema `sales`): `Customer` (legal name, tax ID, addresses, contacts, payment terms, optional credit
  limit, status active/blocked with a reason, allowed sites, invoicing basis `t` or `m3 LOOSE`), `Product` (mapped to a
  material fraction, sale unit), `PriceList` (versions: site, optional customer, currency, effective from, lines per
  product), `SalesOrder`.
- **`Weighbridge`** (schema `weighbridge`): `Hauler`, `Truck` (plate unique per tenant after normalisation, registered
  tare with date and method, legal payload, axles), number blocks, tickets, reversals, delivery notes, daily close.

### 2. Price at a moment, frozen on the ticket

`Sales.Contracts.PriceAt(site, customer, product, instant)`: the customer's list in force at that instant wins over the
site's; no list → no price (the desk says so; the ticket can still be issued and priced later only by a new ticket).
The ticket stores unit price, currency and amount at issue; later price changes never touch it (TC-P3-WB-003).

As built in P3.1 (`IPriceBook.PriceAtAsync`, `GET /api/v1/sales/prices`):

- The instant's **date on the company's clock** (its settings' time zone) picks the versions in force: per list (the
  site's, or one customer's at that site) the active version with the latest `effectiveFrom` on or before that date.
- The rule is per product: when the customer's version in force has no line for the product, the site's price applies.
- A version **in force never changes** (tickets were priced from it): before its first day it can be changed (with
  `If-Match`) or withdrawn; after, a correction is a new version. Prices are **never back-dated** (`effectiveFrom` is
  today or later), and one active version per list starts on a given day (unique index, `NULLS NOT DISTINCT`).
- Products map to a material that is a sold fraction (Materials' `IMaterialDirectory`: kind product, not archived).

### 3. Money and quantities

`Money` (`decimal(18,4)`, ISO 4217 currency) and quantities with their unit; no `double` in these modules (architecture
test). Price fields are sensitive: masked for roles without `sales.prices.read`, in the API and in exports.
`sales.prices.read` is implemented as read access to the sensitive field `sales.prices.unit-price` (Weighmaster and
Executive read; Tenant Admin, Commercial and Finance write; Site Manager and Controller see it masked). Price lists and
the price preview are readable with `sales.products.read`; only the unit price is masked.

## Consequences

- Master data changes at a commercial pace without touching signed evidence; invoicing (P7) reads `Sales` only.
- One more module boundary to keep: the weighbridge asks `Sales` through its contract, never its tables.

## Alternatives considered

| Option | Why not |
|---|---|
| One module for both | Mixes master data with signed evidence and numbering; harder to give invoicing a clean read |
| Trucks in `Sales` | Tares and turnaround rules are weighbridge concerns; trucks are read at the scale, not in quotes |

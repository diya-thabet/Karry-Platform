---
title: "Phase 5 — Inventory & Multi-Warehouse Logistics"
area: phases
tags: [phase-5, inventory, transfers, counts]
source: "PHASES_DETAILED_PLAN.md §12"
status: baseline
last_updated: 2026-10-21
---

# Phase 5 — Inventory & Multi-Warehouse Logistics

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 6 weeks (W26–W31)

## 12.1 Objectives & business value

Stop **spare-part and material leakage** across the *Magasin Général* and site stores, and give **full lifetime traceability** of assets and parts moving between sites. After this phase, **Professional is sellable** and multi-site groups (S2) become a target.

## 12.2 Scope

**In:** warehouse hierarchy, bins, items & UoM, lots/serials, weighted average cost, receipts, issues (to asset/WO), returns, transfers with waybills (dispatch/in-transit/receive/discrepancy), asset/attachment transfers with lifetime history, min/max reorder, RUL-driven stock awareness, purchase requests, physical counts (scheduled/blind/mobile), variance engine & audit cases, inventory dashboards incl. Sankey, multi-site consolidated dashboards.
**Out:** full purchasing (POs, supplier invoices, 3-way match) — export to ERP instead.

## 12.3 Epics

| Epic | Title |
|---|---|
| EP-P5-01 | Warehouses & items |
| EP-P5-02 | Stock movements (receipts, issues, returns, adjustments) |
| EP-P5-03 | Inter-warehouse transfers & waybills |
| EP-P5-04 | Asset & attachment transfers (lifetime history) |
| EP-P5-05 | Replenishment & procurement integration |
| EP-P5-06 | Physical inventory counts & variance |
| EP-P5-07 | Inventory analytics & multi-site dashboards |

## 12.4 User stories & acceptance criteria

### EP-P5-01 Warehouses & items

**US-P5-INV-01 — Warehouse hierarchy** · *Persona: Tenant Admin* · Must · M
- AC1: Warehouse types: Central (Magasin Général), Site store, Workshop store, Fuel (link to fuel storages), Virtual (in transit, scrap, quarantine).
- AC2: Each warehouse linked to a site (or central), with responsible storekeepers (duty assignments with date ranges).
- AC3: Bins/locations optional per warehouse.

**US-P5-INV-02 — Item master** · Must · M
- AC1: Item: code, name FR/EN, category, UoM (with conversions, e.g., box of 12), tracking (none / lot / serial), link to component type (P4), OEM/aftermarket part numbers, min/max per warehouse, valuation method (weighted average), criticality.
- AC2: Import from Excel; barcode/QR label printing.

**US-P5-INV-03 — Opening balances** · Must · S
- AC1: Import opening quantities and unit costs per warehouse/bin/lot, signed by storekeeper and approved by finance.

### EP-P5-02 Stock movements

**US-P5-INV-04 — Goods receipt** · *Persona: Storekeeper* · Must · M
- AC1: Receive items with supplier, reference (delivery note/PO), quantities, unit cost, lots/serials, photos; signed.
- AC2: Updates stock ledger and weighted average cost.
- AC3: Can be linked to procurement alert (closes it as Received).

**US-P5-INV-05 — Issue to asset / work order** · Must · M
- AC1: Issue items to a WO (preferred) or directly to an asset with reason; receiver signs (mechanic) on the device.
- AC2: Serialized items: issuing a serial installs that serial on the WO's component replacement automatically (P4 link).
- AC3: Cost posted to WO and asset.
- AC4: Offline capable on the store tablet.

**US-P5-INV-06 — Returns & scrap** · Must · S
- AC1: Return unused items from WO; return worn parts for scrap/refurbishment to quarantine warehouse with condition.

**US-P5-INV-07 — Stock adjustment** · Must · S
- AC1: Adjustments only through count results or with reason + finance approval (four-eyes); never silent.

**US-P5-INV-08 — Stock ledger** · Must · M
- AC1: Every movement is an append-only stock ledger entry (item, warehouse, bin, lot/serial, qty ±, unit cost, value, document ref).
- AC2: Stock on hand = Σ ledger; balance snapshots for performance; consistency check job.

### EP-P5-03 Transfers & waybills

**US-P5-INV-09 — Create & dispatch transfer** · *Persona: Storekeeper (Magasin Général)* · Must · M
As the central storekeeper, I want to send parts to a quarry with a digital waybill, so that every item is traceable until it is received.
- AC1: Transfer: from warehouse, to warehouse, items/qty/lots/serials, carrier, vehicle, driver, expected arrival.
- AC2: Dispatch signed by sender (and driver); stock moves to "In transit" virtual warehouse; waybill PDF with QR.

**US-P5-INV-10 — Receive transfer** · *Persona: Site Storekeeper* · Must · M
- AC1: Receiver scans waybill QR (offline OK if waybill synced before, or via manual number), confirms quantities per line, records discrepancies (missing, damaged, extra) with photos; signs.
- AC2: Discrepancy creates a transfer case assigned to both storekeepers' manager; quantities in transit stay in "In transit – discrepancy" until resolved.
- AC3: Transfers in transit beyond expected arrival + tolerance → alert.

**US-P5-INV-11 — Transfer requests** · Should · S
- AC1: Site storekeeper requests items from central; central approves/partially fulfils; request status tracked.

### EP-P5-04 Asset & attachment transfers

**US-P5-AST-01 — Transfer an asset between sites** · *Persona: Site Manager / Fleet Manager* · Must · M
- AC1: Asset transfer with waybill (signed dispatch/receive) changes asset assignment (append-only `AssetAssignment`).
- AC2: Meters reading captured at dispatch and receipt; difference (e.g., km driven) validated.
- AC3: Lifetime timeline view: all assignments with per-site totals (hours, km, fuel, maintenance cost, downtime), continuing depreciation (P7).
- AC4: Asset counts per site update usage meters (billing).

**US-P5-AST-02 — Attachments** · Should · S
- AC1: Attachments (buckets, hammers, forks) as assets with parent link; can move between machines and sites; usage hours accrue while attached.

### EP-P5-05 Replenishment

**US-P5-INV-12 — Min/max reorder suggestions** · Must · M
- AC1: Daily job: items below min per warehouse → replenishment suggestion (transfer from central if available, else purchase request).

**US-P5-INV-13 — RUL-driven stock awareness** · Must · M
- AC1: Procurement alerts (P4) check availability across warehouses: same site → reserve; central → propose transfer; none → purchase request.
- AC2: Reservations reduce available quantity; visible on item.

**US-P5-INV-14 — Purchase requests** · Must · S
- AC1: Purchase request document (lines, suggested supplier, needed-by date) exportable (PDF/Excel/CSV) and status tracked (Draft, Sent, Ordered with PO ref, Received).

### EP-P5-06 Physical counts

**US-P5-INV-15 — Count scheduling** · *Persona: Finance / Tenant Admin* · Must · S
- AC1: Schedule full counts every 6 or 12 months per warehouse, and cycle counts (by ABC class) optionally; overdue counts alert.

**US-P5-INV-16 — Mobile blind count** · *Persona: Counter / Storekeeper* · Must · M
- AC1: Count session freezes book quantities snapshot (movements during count are tracked separately).
- AC2: Counters see items/bins without book quantity (blind); scan barcode; enter quantity; offline.
- AC3: Two-person count option for high-value items (second count must match or triggers recount).

**US-P5-INV-17 — Variance analysis & audit case** · Must · M
- AC1: Variance per item = physical − expected ([Computational Engines (Corrected Mathematics) §10.9](../../03-domain/computational-engines.md#109-inventory-variance-engine)), valued at weighted average cost.
- AC2: Tolerances per category; beyond → audit case listing responsible storekeepers (by duty assignment) and movement history since last count.
- AC3: Finance approves the count; approved adjustments posted to stock ledger; all signed.

### EP-P5-07 Analytics & multi-site

**US-P5-ANA-01 — Inventory dashboards** · Must · M
- AC1: Stock value by warehouse & category; slow-moving/dead stock; stock-outs; transfer flows Sankey (central → sites); variance history.

**US-P5-ANA-02 — Multi-site consolidated executive view** · Must · M
- AC1: Executive can switch between site view and group view for all dashboards built so far (integrity, fuel, maintenance, inventory) with site comparison tables.

## 12.5 Technical scope

- Module `Inventory` (Warehouse, Item, StockLedgerEntry, Balance, Transfer, Waybill, Reservation, PurchaseRequest, CountSession, CountLine, AuditCase).
- Integrations: `Maintenance` (alerts ↔ reservations, WO issues), `Assets` (assignments), `Fuel` (fuel warehouses mapping), `Finance` (costs in P7).
- Concurrency: reservations & issues use optimistic concurrency on balances; negative stock forbidden (configurable to allow with flag for offline issues, reconciled on sync).
- Offline: store tablet caches item master for its warehouse; offline issues create pending ledger commands; conflicts (stock insufficient at sync) create "negative stock" exceptions for storekeeper resolution.

**APIs:** `/api/v1/inventory/warehouses`, `/items`, `/balances`, `/receipts`, `/issues`, `/returns`, `/adjustments`, `/transfers` (+ `/dispatch`, `/receive`), `/transfer-requests`, `/reservations`, `/purchase-requests`, `/counts` (+ lines, `/approve`), `/audit-cases`; `/api/v1/assets/{id}/transfers`, `/assets/{id}/timeline`.

## 12.6 Test plan (Phase 5)

| Test ID | Description | Expected |
|---|---|---|
| TC-P5-INV-001 | Weighted average cost after receipts at different prices | Correct WAC to 4 decimals |
| TC-P5-INV-002 | Issue more than available online | Rejected |
| TC-P5-INV-003 | Offline issue causing negative at sync | Exception created, ledger consistent |
| TC-P5-INV-004 | Transfer dispatch → in transit → receive full | Balances correct at each step |
| TC-P5-INV-005 | Receive with 2 missing, 1 damaged | Case created, quantities held |
| TC-P5-INV-006 | Transfer overdue | Alert |
| TC-P5-INV-007 | Serialized part issued to WO | Installed base updated with serial |
| TC-P5-INV-008 | RUL alert with stock at central | Transfer proposed, reservation made |
| TC-P5-INV-009 | Blind count with movements during count | Variance excludes in-count movements correctly |
| TC-P5-INV-010 | Variance within tolerance vs beyond | Only beyond creates audit case |
| TC-P5-INV-011 | Count approval posts adjustments | Ledger entries + signatures |
| TC-P5-INV-012 | Stock ledger vs balance consistency job | 0 discrepancies |
| TC-P5-AST-001 | Asset transfer A→B→A | Timeline continuous, per-site totals correct |
| TC-P5-AST-002 | Usage meter after transfer | Site counts updated |
| TC-P5-ANA-001 | Sankey totals = transfers totals | Match |

**Property tests:** stock conservation (Σ all warehouses incl. virtual = receipts − issues − scrap ± adjustments) for random movement sequences; WAC never negative.

**Performance:** 1M stock ledger entries per tenant; balance query < 200 ms; count session with 5,000 lines on tablet smooth (< 100 ms per scan).

**UAT:** pilot (or second customer with central warehouse) runs a full physical count in Karry and a week of transfers.

## 12.7 Deliverables

Inventory module, transfers/waybills, asset lifetime timeline, replenishment, counts & audit cases, dashboards, consolidated multi-site view, help articles, storekeeper training video.

## 12.8 Demo script

1. Central storekeeper dispatches jaw plates (serialized) to Quarry B; driver signs.
2. Quarry B receives with one damaged plate → discrepancy case.
3. RUL alert on Quarry A cone mantle → system finds a spare at central → proposes transfer.
4. Blind count on tablet; variance beyond tolerance on hydraulic oil → audit case with responsible storekeepers.
5. Loader transferred from A to B; lifetime timeline shows both sites' totals.

## 12.9 Exit gate

- [ ] A full physical count completed in Karry by a customer.
- [ ] Transfer traceable end-to-end with both signatures; discrepancy flow used.
- [ ] Stock conservation property and consistency job green in production for 2 weeks.
- [ ] **Professional plan published**; at least 3 paying Starter/Professional customers onboarded.

## 12.10 Risks

| Risk | Mitigation |
|---|---|
| Item master data is messy at customers | Import wizard with dedup suggestions; onboarding service offering (paid) |
| Overlap with customer ERP inventory | Position as "operational stores" + export; ERP connectors in P9 |

## 12.11 Commercial outcome

**Professional sellable.** Multi-site groups (S2) become the main target. Upsell grows through more sites and the central warehouse.

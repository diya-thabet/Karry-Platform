---
title: "Phase 3 — Weighbridge, Fuel, Integrity & Pilot"
area: phases
tags: [phase-3, weighbridge, fuel, integrity, pilot]
source: "PHASES_DETAILED_PLAN.md §10"
status: baseline
last_updated: 2026-10-21
---

# Phase 3 — Weighbridge, Fuel, Integrity & Pilot

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 5 weeks (W15–W19) · **Team:** full core team + Customer Success (pilot)

## 10.1 Objectives & business value

Close the two biggest **money leaks** (fuel and ticket fraud), give executives the **integrity dashboard**, and **go live at the pilot site in production**. After this phase, **Starter is sellable**.

## 10.2 Scope

**In:** customers, products, price lists, simple sales orders, trucks & registered tares, scale tickets (manual capture with flags), reversals, delivery notes, ticket detection rules, fuel tanks/bowsers, deliveries, dispensing, dips, reconciliation, consumption baselines & anomaly detection v1, integrity dashboard v1, savings report v1, notifications (email/SMS/WhatsApp), demo tenant, production environment, pilot onboarding, support tooling.
**Out:** automatic indicator capture (P6 edge agent), invoicing (export only), advanced pricing (P7+).

## 10.3 Epics

| Epic | Title |
|---|---|
| EP-P3-01 | Customers, products & sales orders (basic) |
| EP-P3-02 | Weighbridge scale tickets |
| EP-P3-03 | Fuel management |
| EP-P3-04 | Fuel analytics & anomaly detection v1 |
| EP-P3-05 | Integrity dashboard & savings report |
| EP-P3-06 | Notifications multi-channel |
| EP-P3-07 | Production readiness & pilot go-live |
| EP-P3-08 | Demo tenant & sales enablement |

## 10.4 User stories & acceptance criteria

### EP-P3-01 Customers, products & sales

**US-P3-SAL-01 — Customers** · *Persona: Commercial Manager* · Must · M
- AC1: Customer: legal name, tax ID, addresses, contacts, payment terms, credit limit (optional), status (active/blocked), allowed sites, invoicing basis (tonnes or m³ LOOSE).
- AC2: Blocked customer cannot receive new tickets (weighmaster sees reason).
- AC3: Import from Excel.

**US-P3-SAL-02 — Products & price lists** · Must · M
- AC1: Products map to materials (fractions) with sale unit (T or m³ LOOSE).
- AC2: Price list per site and optionally per customer with effective dates and currency; price fields maskable.
- AC3: Ticket price resolved at issue time and frozen on the ticket.

**US-P3-SAL-03 — Sales orders (basic)** · Should · M
- AC1: Order: customer, site, product, quantity (with unit), delivery window, status (open, in progress, fulfilled, cancelled).
- AC2: Tickets can be linked to an order; order progress = Σ ticket net quantity.
- AC3: Over-delivery beyond tolerance warns weighmaster.

**US-P3-SAL-04 — Trucks & haulers** · Must · S
- AC1: Truck: plate, hauler/company, registered tare (with date and method), max legal payload, axle config.
- AC2: Unknown truck can be created at the weighbridge (flagged for review).

### EP-P3-02 Weighbridge scale tickets

**US-P3-WB-01 — Issue a scale ticket (two-pass weighing)** · *Persona: Weighmaster* · Must · L→split
As a weighmaster, I want to issue a ticket in under 90 seconds per truck, so that queues stay short and every tonne is billed.
- AC1: First pass (tare or gross depending on flow: inbound/outbound) records weight, truck, time; second pass completes ticket; net computed by system.
- AC2: Single-pass with registered tare allowed if tenant policy permits; flagged `STORED_TARE`.
- AC3: Weight entry source = MANUAL in this phase, with mandatory photo of indicator display (in-app camera).
- AC4: Ticket fields: sequential number per site (gap-free, prefix configurable), truck, driver name, customer, product, order, gross, tare, net (kg & t), converted volume if customer basis is m³ (with density version), price, amount, weighmaster, timestamps of both passes.
- AC5: Weighmaster signs; ticket becomes immutable; printable on A5/thermal 80 mm with QR.
- AC6: Works offline (local numbering reservation blocks per device to keep gap-free sequence per site; ADR required).

**US-P3-WB-02 — Ticket reversal** · *Persona: Weighmaster + Controller* · Must · M
- AC1: Reversal creates a negative ticket referencing the original, with reason; requires weighmaster signature + controller counter-signature.
- AC2: Original shows "Reversed by #…"; a corrected ticket can be issued referencing both.
- AC3: Reversal rate per weighmaster appears on integrity dashboard.

**US-P3-WB-03 — Delivery note** · Must · S
- AC1: Delivery note PDF per ticket (or per order/day) with customer, product, quantity, truck, signatures, QR.
- AC2: Optional customer/driver signature on the tablet at the weighbridge.

**US-P3-WB-04 — Ticket detection rules** · Must · M
- AC1: RULE-WB-01 tare deviation > X % vs registered tare; RULE-WB-02 net > legal payload; RULE-WB-03 same truck two outbound tickets within impossible interval (configurable min turnaround); RULE-WB-04 ticket outside site operating hours; RULE-WB-05 manual weight without indicator photo; RULE-WB-06 reversal rate anomaly per weighmaster; RULE-WB-07 blocked customer override.
- AC2: Flags go to verification queue (policy-dependent) and integrity dashboard.

**US-P3-WB-05 — Weighbridge daily close** · Should · S
- AC1: Weighmaster closes the day: count and total tonnes per product/customer; signature; PDF report.
- AC2: Tickets after close for that date require controller approval.

**US-P3-WB-06 — Ticket export** · Must · S
- AC1: Export tickets per period to CSV/Excel in accounting-friendly format (one line per ticket with customer tax ID, product, qty, unit price, amount, currency).

### EP-P3-03 Fuel management

**US-P3-FUEL-01 — Tanks, bowsers & dispensers** · *Persona: Site Manager* · Must · M
- AC1: Fuel storage: fixed tank / mobile bowser, capacity, product (diesel, petrol, AdBlue), site, calibration table (dip cm → litres, importable CSV), meter (dispenser totalizer).
- AC2: Opening stock recorded with signature.

**US-P3-FUEL-02 — Fuel delivery (receipt)** · *Persona: Fuel Attendant / Storekeeper* · Must · M
- AC1: Record supplier, delivery note number, ordered vs delivered litres, dip before/after, temperature (optional), photos of delivery note and meter.
- AC2: Variance between delivered (supplier) and measured (dip delta) beyond tolerance → flag RULE-FUEL-01.

**US-P3-FUEL-03 — Dispense fuel to an asset** · *Persona: Fuel Attendant* · Must · M
As a fuel attendant, I want to record each refuel against a machine and its meter, so that every litre is accounted for.
- AC1: Scan asset QR → system shows asset, last refuel, last meter, tank capacity of the asset.
- AC2: Capture asset meter reading + photo, litres dispensed, dispenser totalizer before/after (photo), operator present (receiver signs).
- AC3: Validation: litres ≤ asset tank capacity (+ tolerance) → else RULE-FUEL-02; meter reading consistent with shift entries → else RULE-FUEL-03.
- AC4: Works offline; dual signatures (attendant + receiver) on the same device.

**US-P3-FUEL-04 — Tank dips & reconciliation** · Must · M
- AC1: Record periodic dip (daily recommended) with photo.
- AC2: Reconciliation per tank per period: opening + deliveries − dispensed − closing (dip) = variance; variance litres & %, tolerance per tenant (default ±0.5 %).
- AC3: Variance beyond tolerance → RULE-FUEL-04 flag + notification.

**US-P3-FUEL-05 — Fuel to non-asset uses** · Should · S
- AC1: Record dispensing to "other" (generator not registered, external vehicle, sale) with mandatory reason & approval.

### EP-P3-04 Fuel analytics & anomaly detection v1

**US-P3-FUEL-06 — Consumption computation** · Must · M
- AC1: For each refuel-to-refuel interval per asset: litres / ΔH (or ΔKM) using verified shift entries; tagged ESTIMATED when meter in fallback.
- AC2: Daily/weekly/monthly consumption per asset, per type, per site.

**US-P3-FUEL-07 — Baselines & anomaly detection** · Must · M
- AC1: Baseline per asset = rolling median of last N intervals (default 20) with MAD; minimum sample size before alerts (default 8 intervals); until then, use type-level baseline.
- AC2: Anomaly when deviation > threshold (default 18 %) and robust z > 3.5 ([Computational Engines (Corrected Mathematics) §10.6](../../03-domain/computational-engines.md#106-fuel-analytics-engine)) → RULE-FUEL-05.
- AC3: Each anomaly explains: expected vs actual, the interval's refuels and shift entries, possible causes checklist.
- AC4: Additional rules: RULE-FUEL-06 refuel with no usage since last refuel; RULE-FUEL-07 refuel outside shift hours; RULE-FUEL-08 frequent small refuels pattern.

**US-P3-FUEL-08 — Fuel dashboard v1** · *Persona: Executive / Site Manager* · Must · M
- AC1: KPIs: litres per period, L/h per asset type, top 10 assets by deviation, tank variance trend.
- AC2: Scatter: L/h vs hours per asset with anomalies highlighted; heatmap asset × day.
- AC3: Drill-down from any point to underlying refuels and shift entries.

### EP-P3-05 Integrity dashboard & savings report

**US-P3-INT-01 — Integrity dashboard v1** · *Persona: Executive* · Must · M
As an owner, I want one screen that shows where my operation may be losing money or data is suspicious, so that I can act every morning.
- AC1: Tiles: open flags by severity, continuity exceptions, fuel anomalies, tank variances, ticket flags, reversals, pending reviews overdue, clock-skew entries, unsynced devices > 24 h.
- AC2: Trend lines (30/90 days) per flag type; leaderboard by site/controller/weighmaster (permission-gated).
- AC3: Each flag can be assigned, commented, resolved (with reason) and is audited.

**US-P3-INT-02 — Monthly savings & integrity report** · *Persona: Executive* · Must · M
- AC1: Monthly PDF/email: fuel consumption vs baseline period (litres and estimated value at average price), anomalies found/resolved, ticket corrections, paper forms eliminated (count of signed records), approval latency.
- AC2: The method and assumptions behind any estimate ("estimated savings") are stated in the report.
- AC3: Sent automatically on the 1st of each month to executives; available in portal.

### EP-P3-06 Notifications multi-channel

**US-P3-NOT-01 — Email, SMS & WhatsApp channels** · Must · M
- AC1: Channels configurable per notification type and per user preference; quiet hours.
- AC2: Critical alerts (fraud suspected, tank variance, unsafe asset) can go to SMS/WhatsApp (provider adapter), with per-tenant monthly quota (plan-based) and usage metering.
- AC3: Delivery status tracked; failures retried and visible.

### EP-P3-07 Production readiness & pilot go-live

**US-P3-OPS-01 — Production environment** · Must · M
- AC1: Production provisioned via IaC with HA PostgreSQL, PITR, multi-AZ app, WAF, backups, alerting to on-call.
- AC2: Status page live; incident process documented.

**US-P3-OPS-02 — Support tooling** · Must · S
- AC1: In-app "Help & feedback" sends message with correlation ID, app version, device info (with consent).
- AC2: Support inbox / helpdesk integration; support SLAs per plan.

**US-P3-OPS-03 — Pilot onboarding** · Must · M
- AC1: Pilot onboarding playbook ([§20](../../01-product/customer-onboarding-playbook.md)) executed: data import, user creation, device enrollment, QR labels printed and fixed on machines, training sessions (operators, controllers, weighmaster, fuel attendant, managers), cut-over date set.
- AC2: Hypercare: daily check-in for 2 weeks, weekly afterwards.

**US-P3-OPS-04 — Data export for customers** · Must · S
- AC1: Tenant admin can request a full export (ZIP of CSV per entity + documents manifest) — ready within 1 h, link expires after 7 days.

### EP-P3-08 Demo tenant & sales enablement

**US-P3-GRW-01 — Demo tenant generator** · *Persona: Sales* · Must · M
- AC1: One command/button creates a demo tenant with realistic data: 1–3 sites, 40 assets, 90 days of shifts, fuel, tickets, including seeded anomalies and exceptions.
- AC2: Demo tenants auto-expire after 30 days; data obviously fictional (company names).

**US-P3-GRW-02 — ROI calculator (internal tool)** · Should · S
- AC1: Simple calculator (spreadsheet or web page) estimating savings from fuel volume, number of machines, tickets/day, with conservative assumptions, used by sales.

## 10.5 Technical scope

**Modules:** `Weighbridge` (Ticket, Reversal, DeliveryNote, Truck, DailyClose), `Sales` (Customer, Product, PriceList, Order), `Fuel` (Storage, Delivery, Dispense, Dip, Reconciliation, Baseline), `Integrity` (flags & cases, dashboard read models), `Notifications` (channels), `Reporting` (PDF generation via QuestPDF, scheduled reports).

**Ticket numbering ADR:** each enrolled weighbridge device reserves a block of numbers per site while online (e.g., 200 numbers); if offline and block exhausted, ticket issued with provisional number `P-<device>-<seq>`, then renumbered on sync with link kept. Legal acceptability of this approach is confirmed in P0 legal memo.

**Analytics:** consumption computation & baselines in .NET (deterministic); robust statistics library; read models updated by events.

**Key APIs:** `/api/v1/sales/customers`, `/products`, `/price-lists`, `/orders`; `/api/v1/weighbridge/tickets` (+ `/reverse`, `/pdf`), `/trucks`, `/daily-close`; `/api/v1/fuel/storages`, `/deliveries`, `/dispenses`, `/dips`, `/reconciliations`, `/consumption`, `/anomalies`; `/api/v1/integrity/flags`, `/cases`, `/dashboard`; `/api/v1/reports/monthly-integrity`.

## 10.6 Test plan (Phase 3)

| Test ID | Description | Expected |
|---|---|---|
| TC-P3-WB-001 | Two-pass outbound ticket | Net = gross − tare, number sequential, PDF QR verifies |
| TC-P3-WB-002 | Customer basis m³ | Volume computed with LOOSE density version at issue time |
| TC-P3-WB-003 | Price change after ticket | Ticket price frozen |
| TC-P3-WB-004 | Edit ticket via API | Not allowed (405/409) |
| TC-P3-WB-005 | Reversal without controller countersign | Stays pending, not effective |
| TC-P3-WB-006 | Offline ticket numbering, block exhaustion | Provisional number, renumbered on sync, link kept, no gap |
| TC-P3-WB-007 | Two devices same site offline | No duplicate numbers |
| TC-P3-WB-008..014 | RULE-WB-01..07 positive/negative | Correct flags |
| TC-P3-WB-015 | Blocked customer | Ticket refused unless override (audited) |
| TC-P3-FUEL-001 | Dispense > asset tank capacity | RULE-FUEL-02 flag |
| TC-P3-FUEL-002 | Reconciliation with deliveries, dispenses, dips | Variance correct to 0.01 L |
| TC-P3-FUEL-003 | Calibration table interpolation | Correct litres for intermediate dips |
| TC-P3-FUEL-004 | Baseline with < 8 intervals | Uses type baseline; no false alerts |
| TC-P3-FUEL-005 | Injected 25 % overconsumption in golden dataset | Detected |
| TC-P3-FUEL-006 | Normal variation (±10 %) | Not flagged |
| TC-P3-FUEL-007 | Meter in fallback | Consumption tagged ESTIMATED, anomaly threshold relaxed per config |
| TC-P3-FUEL-008 | Golden dataset (pilot month) recompute | Matches domain expert computation within 1 % |
| TC-P3-INT-001 | Flag lifecycle assign → resolve | Audited, removed from open counts |
| TC-P3-INT-002 | Leaderboard permission | Hidden for roles without permission |
| TC-P3-RPT-001 | Monthly report generation for 3 sites | Correct totals vs SQL ground truth |
| TC-P3-NOT-001 | WhatsApp/SMS quota exceeded | Falls back to email, admin informed |
| TC-P3-OPS-001 | Full tenant export | All entities present, row counts match |
| TC-P3-OPS-002 | Production DR: restore to new instance | Within RTO 4 h, ledger verifies |
| TC-P3-GRW-001 | Demo tenant generation | Dashboards populated, anomalies visible |

**Property tests:** reconciliation identity holds for random sequences; ticket net never negative except reversal; numbering gap-free across random online/offline interleavings (model-based test).

**Performance:** weighbridge peak: 60 tickets/hour per site × 50 sites concurrently; integrity dashboard < 2 s for 12 months of data on one site.

**Security:** penetration-style internal review on ticket immutability and reversal workflow; WhatsApp/SMS provider credentials stored in vault; webhook signatures verified.

**UAT & go-live:** cut-over rehearsal on staging with pilot data; go/no-go meeting; live for 2 weeks with hypercare; daily defect triage.

## 10.7 Deliverables

- Weighbridge, sales basics, fuel management, anomaly detection v1, integrity dashboard, monthly report.
- Production environment, status page, support process, on-call rotation.
- Pilot live; training material (FR/EN videos ≤ 3 min per role, printable quick guides).
- Demo tenant generator, ROI calculator, sales deck v1, pricing page draft.

## 10.8 Demo script

1. Weighmaster issues a ticket with photo of indicator; prints thermal ticket; scans QR.
2. Reversal flow with controller countersignature.
3. Fuel attendant refuels a loader offline (dual signature); dip and reconciliation show a variance → alert received on WhatsApp.
4. Executive opens integrity dashboard: drills from a fuel anomaly to the exact refuels and shift photos.
5. Monthly integrity & savings report PDF.

## 10.9 Exit gate (M2 — Pilot MVP)

- [ ] Pilot site runs ≥ 2 consecutive weeks with no paper for shifts, fuel and tickets.
- [ ] Zero data loss incidents; no Sev-1 during pilot weeks; Sev-2 fixed within SLA.
- [ ] Fuel baseline established for ≥ 80 % of pilot machines.
- [ ] Pilot customer agrees to convert to paid subscription (or to a case study) at end of pilot.
- [ ] Starter plan published with price book; contract templates (MSA, DPA, SLA) ready.

## 10.10 Risks

| Risk | Mitigation |
|---|---|
| Weighbridge queues increase due to new process | Optimize UX, pre-fill from order, keyboard-first desktop flow, measure time per truck |
| Pilot staff turnover / resistance | Champions per shift, short training videos, management mandate, incentives |
| False positives erode trust in alerts | Conservative defaults, minimum sample sizes, explanation for each alert, fast threshold tuning |
| Legal concern about provisional ticket numbers | Policy option: forbid offline tickets if legal memo requires; keep online-only mode |

## 10.11 Commercial outcome

**Starter becomes sellable.** We have a live reference customer, real before/after data for the first case study, a demo tenant for every sales call, and the first paying customers onboarded through the same playbook.

---
title: "Phase 9 — Integrations, Telematics & Public API"
area: phases
tags: [phase-9, telematics, api, sso]
source: "PHASES_DETAILED_PLAN.md §16"
status: baseline
last_updated: 2026-10-21
---

# Phase 9 — Integrations, Telematics & Public API

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 6 weeks (W50–W55), in parallel with P10 (requires 2 squads or extended timeline)

## 16.1 Objectives & business value

Unlock **Enterprise** deals: integrate with OEM telematics, ERPs, plant automation, and offer a public API. Telematics also gives an **independent cross-check** of manual meter readings, which is the strongest anti-fraud signal available.

## 16.2 Scope

**In:** ISO 15143-3 (AEMP 2.0) telematics connectors, telematics cross-check rules, edge agent v2 (more indicator protocols, ANPR, barrier, OPC UA/Modbus for belt scales & PLCs), ERP/accounting connectors (Odoo, Sage; generic), webhooks, public REST API v1 with OAuth clients & API keys, developer portal, document RAG for copilot, SSO federation (SAML/OIDC), first service extraction (telemetry ingestion) if triggers met.
**Out:** custom per-customer integrations (handled as paid professional services using the public API).

## 16.3 Epics

| Epic | Title |
|---|---|
| EP-P9-01 | Telematics ingestion & cross-check |
| EP-P9-02 | Edge agent v2 & plant automation |
| EP-P9-03 | ERP & accounting connectors |
| EP-P9-04 | Public API, webhooks & developer portal |
| EP-P9-05 | Enterprise identity (SSO federation, SCIM) |
| EP-P9-06 | Document RAG for copilot |
| EP-P9-07 | Telemetry service extraction |

## 16.4 User stories & acceptance criteria

### EP-P9-01 Telematics

**US-P9-TEL-01 — AEMP 2.0 connector** · *Persona: Tenant Admin* · Must · M
- AC1: Admin configures OEM telematics credentials (OAuth / API key) per fleet; Karry polls ISO 15143-3 endpoints (cumulative operating hours, fuel used, location, idle hours, fault codes) on schedule.
- AC2: Telematics assets mapped to Karry assets (by serial/VIN, with manual matching UI).
- AC3: Data stored in Timescale hypertables; connection health visible; credentials in vault.

**US-P9-TEL-02 — Telematics vs manual cross-check** · Must · M
- AC1: RULE-TEL-01: manual ΔH vs telematics ΔH per shift differs > tolerance → flag. RULE-TEL-02: fuel dispensed vs telematics fuel used deviation → flag. RULE-TEL-03: machine location outside site geofence during shift → flag. RULE-TEL-04: engine running outside any recorded shift → flag.
- AC2: Telematics hours can feed RUL as primary source when enabled (manual readings kept as verification).

**US-P9-TEL-03 — Fault codes to maintenance** · Should · M
- AC1: Critical OEM fault codes create breakdown requests/WOs suggestions with code description.

### EP-P9-02 Edge v2

**US-P9-EDG-01 — More indicator protocols & ANPR** · Must · M
- AC1: Protocol library extended to the top indicator brands in target markets; protocol sniffing tool for installers.
- AC2: ANPR camera integration: plate read at entry, matched to truck; mismatch flagged.
- AC3: Barrier control linked to ticket state (open only after ticket issued).

**US-P9-EDG-02 — OPC UA / Modbus for belt scales & PLC** · Should · M
- AC1: Read totalizers and running status from belt scales and PLC tags; map tags to plant nodes; feed production runs automatically (manual entries become verification).

### EP-P9-03 ERP & accounting

**US-P9-API-01 — Odoo & Sage connectors** · Should · M
- AC1: Push invoices/ticket lines, cost centre journals, purchase requests; pull customers & products (master data sync direction configurable).
- AC2: Sync log with errors & retry; idempotent.

**US-P9-API-02 — Generic file-based integration** · Must · S
- AC1: Scheduled SFTP/email export of configured datasets in CSV.

### EP-P9-04 Public API & webhooks

**US-P9-API-03 — Public REST API v1** · *Persona: Customer developer / Integrator* · Must · M
- AC1: Read endpoints for assets, shift entries (verified), tickets, fuel, WOs, inventory balances, KPIs; selected write endpoints (create customers, orders, external meter readings).
- AC2: OAuth 2.1 client credentials + API keys (scoped, rotatable, expiring); rate limits per plan; usage metered.
- AC3: Versioned, documented (OpenAPI 3.1), with examples, SDK snippets.

**US-P9-API-04 — Webhooks** · Must · M
- AC1: Subscribe to events (ticket.issued, ticket.reversed, entry.verified, flag.raised, wo.completed, transfer.received, …); HMAC-signed payloads; retries with exponential backoff for 24 h; delivery log; replay.

**US-P9-API-05 — Developer portal** · Should · S
- AC1: Docs site with guides, API reference, changelog, sandbox tenant credentials.

### EP-P9-05 Enterprise identity

**US-P9-IAM-01 — SSO federation** · Must · M
- AC1: Tenant admin configures SAML 2.0 or OIDC IdP (Entra ID, Google, Okta); JIT provisioning with role mapping by group claims; enforce SSO for office users.

**US-P9-IAM-02 — SCIM user provisioning** · Could · M
- AC1: SCIM 2.0 endpoint for user/group sync.

### EP-P9-06 Document RAG

**US-P9-AI-01 — Document library & Q&A** · Should · M
- AC1: Upload manuals, SOPs, contracts (PDF/DOCX); text extracted, chunked, embedded (pgvector), tenant-scoped, permission-tagged.
- AC2: Copilot tool `search_documents` returns passages with document/page citations.

### EP-P9-07 Telemetry extraction

**US-P9-PLT-01 — Telemetry ingestion service** · Should (conditional on [§3.6](../../02-architecture/modular-monolith-decision.md#36-extraction-triggers-when-we-will-create-a-microservice) triggers) · L→split
- AC1: Separate deployable consuming telematics/edge streams via message broker, writing to Timescale; core monolith subscribes to aggregated events.
- AC2: No change for clients; module boundary unchanged.

## 16.5 Test plan (Phase 9)

| Test ID | Description | Expected |
|---|---|---|
| TC-P9-TEL-001 | AEMP mock server: hours, fuel, location | Ingested, mapped |
| TC-P9-TEL-002 | Manual vs telematics ΔH mismatch | RULE-TEL-01 flag |
| TC-P9-TEL-003 | OEM API outage / rate limit | Backoff, health degraded, no data loss on recovery |
| TC-P9-EDG-001 | ANPR mismatch | Flag |
| TC-P9-EDG-002 | OPC UA simulator tags | Production run auto-filled |
| TC-P9-API-001 | API key scope enforcement | 403 outside scope |
| TC-P9-API-002 | Rate limit per plan | 429 with Retry-After |
| TC-P9-API-003 | Webhook signature & retry | Valid HMAC; retries; replay works |
| TC-P9-API-004 | Public API tenant isolation | Isolation suite extended, passes |
| TC-P9-IAM-001 | SAML login with group → role mapping | Correct roles |
| TC-P9-AI-001 | Document Q&A with permission-tagged doc | Not returned to unauthorized role |
| TC-P9-PLT-001 | Telemetry service throughput | ≥ 5,000 points/s sustained |

**Security:** external review of public API & webhooks; secrets rotation drill.

## 16.6 Deliverables

Telematics connectors, cross-check rules, edge v2, ERP connectors, public API + webhooks + developer portal, SSO federation, document RAG, (conditional) telemetry service.

## 16.7 Exit gate

- [ ] At least one OEM telematics integration live with a customer.
- [ ] Public API used by at least one integrator / customer.
- [ ] **Enterprise plan published** with SSO, API, dedicated hosting option.

## 16.8 Commercial outcome

**Enterprise sellable**, plus the partner ecosystem: OEM dealers, weighbridge installers and ERP integrators can build on Karry.

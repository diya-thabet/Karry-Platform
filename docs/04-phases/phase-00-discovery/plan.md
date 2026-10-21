---
title: "Phase 0 — Discovery & Architecture Baseline"
area: phases
tags: [phase-0, discovery, spikes]
source: "PHASES_DETAILED_PLAN.md §7"
status: baseline
last_updated: 2026-10-21
---

# Phase 0 — Discovery & Architecture Baseline

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 3 weeks (W1–W3) · **Team:** Architect, PO, UX, 1 senior backend, 1 senior frontend (spikes), domain expert (advisory), founder (commercial validation)

## 7.1 Objectives & business value

- **Validate that customers will pay**, and how much, before writing production code.
- Validate the domain (formulas, workflows, vocabulary) with real quarry staff.
- Remove the top technical risks with spikes (offline, crypto, RLS, extensions).
- Produce a backlog for P1–P3 detailed enough that the team starts P1 at full speed.

## 7.2 Scope

**In:** customer interviews, site visits, event storming, formula validation, legal review, UX prototype, technical spikes, ADRs, pricing validation, pilot agreement, backlog.
**Out:** production code (spike code is thrown away or moved into P1 only after review).

## 7.3 Work packages (Phase 0 uses work packages instead of user stories)

| ID | Work package | Owner | Output | Acceptance |
|---|---|---|---|---|
| WP-P0-01 | **Customer discovery interviews** (≥ 8 quarry companies across ICP segments) | Founder + PO | Interview notes, pain ranking, willingness-to-pay matrix | ≥ 8 interviews; top 5 pains ranked by ≥ 6 interviewees; price sensitivity range per plan documented |
| WP-P0-02 | **Site visits** (≥ 2 quarries, including one shift change, weighbridge, fuel dispensing, store) | PO + UX + Architect | Photo-documented process maps (as-is) | As-is BPMN maps for: shift start/end, fueling, weighing, store issue, transfer, maintenance stop |
| WP-P0-03 | **Event storming** (big picture → process level) | Architect | Domain events timeline, aggregates, bounded contexts v1.1 | Context map signed by Architect + PO + domain expert; changes vs master plan recorded in ADR-0001 |
| WP-P0-04 | **Formula validation** with mining engineer (all items in [Specification Review & Corrections §3](../../03-domain/spec-review-corrections.md), [Computational Engines (Corrected Mathematics) §10](../../03-domain/computational-engines.md)) | Architect + domain expert | `docs/03-domain/formulas-worked-examples.md` with worked numeric examples | Every formula has: definition, units, valid ranges, worked example, source, sign-off |
| WP-P0-05 | **Golden datasets** collected from pilot candidate (Excel shift sheets, fuel logs, tickets, maintenance logs) | PO | Anonymised dataset in `tests/golden/` | ≥ 1 month of real data for shifts, fuel, tickets; ≥ 1 year of maintenance history if available |
| WP-P0-06 | **Legal review**: e-signature validity, data protection, retention, invoicing rules, in target country(ies) | Founder + counsel | Legal memo | Signature level achievable confirmed; retention periods table; DPA template drafted |
| WP-P0-07 | **Pricing validation**: test plan packaging ([§2.3](../../01-product/business-model-and-pricing.md#23-subscription-plans-illustrative-validated-in-phase-0-with-real-prospects)) with ≥ 5 prospects | Founder | Validated price book v1 | ≥ 3 prospects confirm Starter or Professional price as acceptable; ≥ 1 signed pilot LOI |
| WP-P0-08 | **Personas & journey maps** | UX | Personas, journeys for 6 key roles | Validated with at least one real person per persona |
| WP-P0-09 | **Clickable prototype** (field PWA: shift start/end, fuel, ticket; portal: controller queue, integrity dashboard) | UX | Figma prototype | Usability test with ≥ 5 field users outdoors on a tablet; task success ≥ 80 % without help; issues logged |
| WP-P0-10 | **Spike: offline capture** (Workbox + Dexie + photo capture on 2 GB Android tablet, 72 h airplane mode, 1,000 entries + 300 photos) | Senior FE | Spike report + code | No data loss after app kill / reboot; storage persisted; sync of backlog < 5 min on 4G |
| WP-P0-11 | **Spike: device keys** (WebCrypto ECDSA non-extractable key in IndexedDB; survives restart; signs offline; server verifies) | Senior FE + BE | Spike report | Key survives 10 browser restarts and OS reboot on Chrome Android; signature verifies server-side; behaviour on "clear site data" documented |
| WP-P0-12 | **Spike: RLS + pooling** (Npgsql pool, `SET LOCAL app.tenant_id`, EF Core) | Senior BE | Spike report + benchmark | 10,000 concurrent mixed-tenant requests with zero cross-tenant leakage; overhead < 5 % latency |
| WP-P0-13 | **Spike: PostgreSQL version & extensions on target provider** (PostGIS, TimescaleDB, pgvector availability on managed service) | DevOps | Decision record | ADR-0003 approved with provider + PG major version |
| WP-P0-14 | **Hosting & region decision** (cost, residency, latency to target sites, managed services) | Architect + Founder | ADR | Cost estimate for 12 months; region chosen |
| WP-P0-15 | **Billing provider decision** (based on incorporation country & target markets) | Founder | ADR | Provider chosen; fee model understood |
| WP-P0-16 | **ADRs 0001–0016 written** | Architect | `docs/adr/` | All reviewed; status Accepted or explicitly Deferred |
| WP-P0-17 | **Backlog P1–P3** | PO + Architect | Epics & stories meeting DoR for first 2 sprints of P1 | Sprint 1 & 2 fully ready; P2–P3 stories at least titled + AC drafted |
| WP-P0-18 | **Threat model v1** (STRIDE on trust layer, sync, tenancy) | Architect | `docs/02-architecture/threat-model.md` | Top 15 threats with mitigations mapped to stories |
| WP-P0-19 | **Brand & naming check** (trademark search "Karry", domain names, logo brief) | Founder | Report | Name is clear to use in target markets or alternative chosen |
| WP-P0-20 | **Success metrics for pilot** agreed with pilot customer | PO + Founder | Pilot charter | Baseline metrics defined (fuel L/h, ticket count, paper forms eliminated, approval latency) |

## 7.4 Technical scope (spikes only)

- Throwaway repos under `spikes/` (not merged into `src/`).
- Spike report template: question, approach, results with numbers, decision, risks remaining.

## 7.5 Test plan (Phase 0)

| Test | Method | Pass criteria |
|---|---|---|
| Prototype usability | Moderated tests with 5+ field users, outdoors, gloves if usual | Task success ≥ 80 %, SUS score ≥ 70 |
| Offline spike | Scripted 72 h airplane mode, kill app, reboot, low battery | 0 lost records |
| Crypto spike | Automated sign/verify across 10 restarts | 100 % verification |
| RLS spike | k6 mixed-tenant load with leak detector | 0 leaks |
| Formula review | Domain expert recomputes golden examples by hand | 100 % match |

## 7.6 Deliverables

1. Validated personas, journeys, as-is process maps.
2. Context map v1.1 and domain glossary FR/EN.
3. `docs/03-domain/formulas-worked-examples.md` (signed).
4. Legal memo + DPA template.
5. Price book v1 + pilot LOI/contract.
6. Clickable prototype + usability report.
7. Spike reports (4) + ADRs (16).
8. Threat model v1.
9. Groomed backlog P1–P3.
10. Updated architecture docs (`docs/02-architecture/`, `docs/03-domain/`) v1.1.

## 7.7 Demo script (end of P0)

1. Walk through the clickable prototype as an operator starting a shift offline.
2. Show the controller queue prototype.
3. Present the spike results with numbers (offline, crypto, RLS).
4. Present validated pricing and the pilot charter.

## 7.8 Exit gate (M0)

- [ ] ≥ 1 signed pilot agreement (LOI minimum) with baseline metrics.
- [ ] Formulas signed off by domain expert.
- [ ] All 4 spikes green, or with mitigations accepted.
- [ ] ADRs accepted.
- [ ] Sprint 1–2 backlog meets DoR.
- [ ] Budget and team for P1–P3 confirmed.

## 7.9 Risks

| Risk | Mitigation |
|---|---|
| Cannot find pilot customer | Offer free pilot for 3 months against case-study rights + feedback commitment |
| Domain expert unavailable | Contract a freelance mining/quarry engineer for 10 days |
| WebCrypto key persistence unreliable on target devices | Fallback: server-issued short-lived signing tokens + server signature; device signature as best-effort |

## 7.10 Commercial outcome

Validated pricing, a pilot customer, and the first marketing assets (problem statement, prototype screenshots).

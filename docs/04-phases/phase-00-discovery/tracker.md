---
title: "Phase 0 Tracker: Work Packages, Evidence & Exit Gate"
area: phases
tags: [phase-0, tracker, status, spikes, exit-gate, test-cases]
source: "Phase 0 execution"
status: draft
last_updated: 2026-10-22
---

# Phase 0 Tracker: Work Packages, Evidence & Exit Gate

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 0 plan](plan.md)

Live status of [Phase 0](plan.md). **Owner** = repository owner / founder (human work); **Agent** = AI engineering agent. Update this file whenever a work package moves.

Legend: ✅ done · 🟡 in progress / partially done · ⏳ waiting on owner or external input · ⬜ not started

## 1. Work packages

| WP | Work package | Who | Status | Evidence / next action |
|---|---|---|---|---|
| WP-P0-01 | Customer discovery interviews (≥ 8) | Owner | ⏳ | Kit ready: [interview-guide.md](../../01-product/discovery/interview-guide.md) |
| WP-P0-02 | Site visits (≥ 2, incl. shift change) | Owner + architect | ⏳ | Kit ready: [site-visit-checklist.md](../../01-product/discovery/site-visit-checklist.md) |
| WP-P0-03 | Event storming → context map v1.1 | Architect + domain expert | ⬜ | Needs WP-P0-02 input; baseline in [bounded-contexts.md](../../03-domain/bounded-contexts.md) |
| WP-P0-04 | Formula validation with worked examples | Agent → domain expert | 🟡 | Draft with 22 golden examples: [formulas-worked-examples.md](../../03-domain/formulas-worked-examples.md). **Needs expert sign-off** |
| WP-P0-05 | Golden datasets from pilot candidate | Owner | ⏳ | Needs pilot customer (WP-P0-07) |
| WP-P0-06 | Legal review | Owner + counsel | ⏳ | Brief ready: [legal-questions-for-counsel.md](../../01-product/discovery/legal-questions-for-counsel.md) |
| WP-P0-07 | Pricing validation (≥ 5 prospects) + pilot LOI | Owner | ⏳ | Method ready: [pricing-validation.md](../../01-product/discovery/pricing-validation.md) |
| WP-P0-08 | Personas & journey maps | UX + owner | ⬜ | Baseline personas in [personas-and-use-cases.md](../../01-product/personas-and-use-cases.md); validate with real people |
| WP-P0-09 | Clickable prototype + usability test | UX | ⬜ | Spike PWA proves the offline flows; hi-fi prototype still needed |
| WP-P0-10 | Spike: offline capture | Agent | 🟡 | ✅ Automated (desktop Chromium): [spike report](../../../spikes/field-capture-pwa/README.md). ⏳ Device-lab run (§3) |
| WP-P0-11 | Spike: device keys | Agent | ✅ | [PWA report](../../../spikes/field-capture-pwa/README.md), [.NET interop](../../../spikes/signature-interop/README.md) |
| WP-P0-12 | Spike: RLS + pooling | Agent | ✅ | [spike report](../../../spikes/rls-pooling/README.md): 0 leaks; policy cost +2.4–4.0 %; batching required |
| WP-P0-13 | Spike: PostgreSQL version & extensions | Agent → owner | 🟡 | ✅ Self-hosted images verified in CI (§2): PG 17.11 and 18.6 both provide PostGIS 3.6.4, TimescaleDB 2.31.0, pgvector 0.8.7, pgcrypto. ⏳ Managed-provider support depends on WP-P0-14 |
| WP-P0-14 | Hosting & region decision | Owner + architect | ⏳ | Needs target countries (open question 1) |
| WP-P0-15 | Billing provider decision | Owner | ⏳ | Needs incorporation country (open question 13) |
| WP-P0-16 | ADRs 0001–0016 | Agent → owner | 🟡 | All 16 written ([ADR index](../../adr/README.md)), status **Proposed**; owner accepts at M0 |
| WP-P0-17 | Backlog P1–P3 ready | Architect + PO | 🟡 | Stories + AC exist ([story index](../story-index.md)); sprint 1–2 refinement after spikes |
| WP-P0-18 | Threat model v1 | Agent | ✅ | [threat-model.md](../../02-architecture/threat-model.md) (17 threats mapped to stories/tests) |
| WP-P0-19 | Brand & trademark check | Owner | ⏳ | Question 13 in the legal brief |
| WP-P0-20 | Pilot success metrics | Owner + pilot customer | ⏳ | Template ready: [pilot-charter-template.md](../../01-product/discovery/pilot-charter-template.md) |

## 2. Automated test cases (spikes)

New to spikes? Read [Technical Spikes Explained (Simple English)](spikes-explained.md) first.

All run in GitHub Actions ([`spikes.yml`](../../../.github/workflows/spikes.yml)).

| Test case | Spike | Local (2026-10-21) | GitHub Actions (2026-10-22) |
|---|---|---|---|
| TC-P0-RLS-001 … 010 | rls-pooling (.NET, PostgreSQL 17.5) | ✅ 11/11 (incl. 2 theory cases) | ✅ |
| TC-P0-SIG-001 … 004 | signature-interop (.NET) | ✅ 4/4 | ✅ |
| TC-P0-OFF-001 … 005 | field-capture-pwa (Playwright, Chromium) | ✅ 5/5 | ✅ |
| TC-P0-KEY-001 … 005 | field-capture-pwa (Playwright, Chromium) | ✅ 5/5 | ✅ |
| Extension check pg17 / pg18 | pg-extensions (Docker) | n/a (no Docker locally) | ✅ pg17 + pg18 |

GitHub Actions: all 4 jobs green in 4 consecutive runs (39828081288, 39828265888, 39828509297, 39828751049). Mutation checks were done for both code spikes (injected bugs were caught; details in each report).

## 3. Manual device-lab checklist (WP-P0-10 remainder)

Run on a **real low-end Android tablet** (2 GB RAM, Android 10–12, Chrome), using the spike app (`pnpm build && pnpm preview --host`, then install as PWA):

- [ ] Install PWA, go to airplane mode, cold start < 3 s
- [ ] Capture 1,000 entries + 300 photos over 72 h in airplane mode
- [ ] Kill the app and reboot the device mid-way; no data lost
- [ ] Low battery (< 15 %) and battery-saver mode; capture still works
- [ ] `navigator.storage.persist()` granted? (record result per device/browser)
- [ ] Reconnect on throttled 3G: full backlog syncs in < 15 min, exactly once
- [ ] Sunlight readability and glove use of the capture screen (notes for UX)

## 4. M0 exit gate

| Criterion (from [plan](plan.md)) | Status |
|---|---|
| ≥ 1 signed pilot agreement (LOI) with baseline metrics | ⏳ owner |
| Formulas signed off by domain expert | ⏳ draft ready |
| All 4 spikes green, or with mitigations accepted | 🟡 4 answered in CI; managed-provider extension support pending WP-P0-14 |
| ADRs accepted | ⏳ 16 proposed, awaiting owner |
| Sprint 1–2 backlog meets DoR | 🟡 |
| Budget and team for P1–P3 confirmed | ⏳ owner |

**Gate decision:** not yet; Phase 0 is in progress.

## 5. Findings that changed the plan

1. **RLS policy must use `nullif(current_setting('app.tenant_id', true), '')::uuid`**: the documented form throws on reused sessions (TC-P0-RLS-004). Docs updated.
2. **Tenant `set_config` must be batched with the first statement**: a separate round trip costs ~+14 % on single-query transactions. This changes US-P1-TEN-02 AC2.
3. **Server signature verification must use IEEE P1363**, not DER (TC-P0-SIG-004). Recorded in ADR-0007.
4. **.NET 10 SDK requires the Microsoft Testing Platform runner** for `dotnet test` with xunit v3 (`global.json` → `test.runner`). Recorded in ADR-0002.
5. **Playwright offline emulation does not block routed requests**, so test fakes must model outages (spike `FakeSyncServer`).
6. **The extension check raced PostgreSQL's init restart** (intermittent pg18 failure): readiness is now probed over TCP. Same rule applies to Testcontainers/health checks in Phase 1.

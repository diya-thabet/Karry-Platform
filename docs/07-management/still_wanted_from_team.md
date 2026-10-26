---
title: "Still Wanted From the Team: Every Open Owner Input, Decision and Item"
area: management
tags: [owner, inputs, decisions, providers, accounts, hardware, questions, checklist]
source: "collected 2026-10-25 from the hand-off log, Phase 0/1 trackers, sub-phase cards and phase plans P2–P10"
status: live
last_updated: 2026-10-26
---

# Still Wanted From the Team

> [Docs index](../README.md) › [07-management](README.md)

**One list of everything the project is waiting for from the owner and the team**: decisions, providers, accounts, data,
hardware, reviews and answers. It covers every phase. Each item says **when it is needed** and **what happens if it is
late**, so nothing blocks silently.

**How to use it.** When you answer an item, tell the agent (or write the answer in the linked doc). The agent records
the answer in the right place, then ticks the item here with the date (`✅ 2026-10-30: answer`). Items are never just deleted;
answered ones move to §9 so the history stays visible. The agent keeps this file in step with the
[hand-off log §7–§8](../00-llm/llm_hands_off.md#7-open-questions-awaiting-the-product-owner) and the
[Phase 1 owner-input table](../04-phases/phase-01-platform-foundation/sub-phases.md#5-owner-inputs-and-when-they-are-needed).

Legend: 🔴 needed now / blocks the next step · 🟠 needed within Phase 1 · 🟡 needed in a later phase · ⚪ nice to have

---

## 1. Needed now (🔴)

| # | What is wanted | Needed by | If late | Where it is used |
|---|---|---|---|---|
| N-01 | **Plan limits and prices** (Starter / Professional / Business / Enterprise: sites, active assets, users, SMS/WhatsApp quota, AI quota, storage, prices per month and year, currency) | **P1.10 (now)** | Illustrative values from the business model are seeded; final values are a data change later, not code | [Business model §2.3](../01-product/business-model-and-pricing.md#23-subscription-plans-illustrative-validated-in-phase-0-with-real-prospects), [P1.10 card](../04-phases/phase-01-platform-foundation/sub-phases.md#p110--plans-entitlements-subscriptions--feature-flags) |
| N-02 | **E-mail sender domain** for invitations and notifications (domain name + DNS access to add SPF, DKIM, DMARC) | P1.8 (overdue; P1.17 at the latest) | Invitations only go to Mailpit locally; no real e-mail can be sent | [P1.8 card](../04-phases/phase-01-platform-foundation/sub-phases.md#p18--identity--authentication) |
| N-03 | **SMS / WhatsApp provider** (for example Twilio, Infobip, Vonage, Orange/MTN local aggregator, Meta WhatsApp Business): account, sender ID, target countries | P1.8 (overdue; P1.17 at the latest) | Phone invitations are previewed in Mailpit and refused outside development; critical alerts by SMS (Phase 3) cannot ship | P1.8, [Phase 3 plan](../04-phases/phase-03-weighbridge-fuel-pilot/plan.md) |
| N-04 | **Install the Renovate GitHub app** (this repository only) and approve its first "Configure Renovate" PR | Now | Dependency updates stay manual; security patches arrive later | `renovate.json`, [hand-off §8](../00-llm/llm_hands_off.md#8-next-steps-prioritised) |
| N-05 | **Review the competitor analysis** (`~/Desktop/Karry-Competitor-Analysis.md`): it recommends a Guinea-first beachhead and deploying at Phase 3 / M2. Accept, change or reject | Now (drives N-07, N-08) | The agent keeps the current plan; market choice stays open | [hand-off §8](../00-llm/llm_hands_off.md#8-next-steps-prioritised) |
| N-06 | **Default role and permission matrix review** (12 roles; who sees prices, costs, wages) | Done in code (P1.9); your review is still welcome | The documented defaults stay; changes later are a release note | [Role matrix](../03-domain/catalogues/roles-and-permissions.md) |

## 2. Business decisions that drive many others

| # | Decision wanted | Needed by | If late | Drives |
|---|---|---|---|---|
| B-01 | **Target countries / first market** (open question 1) | Before P1.17 | Hosting region, currency, languages, legal and payment provider stay open | N-07, N-08, B-02, L-01 |
| B-02 | **Company incorporation country** (open question 13) | Before Phase 10 (billing); earlier for contracts | Billing provider cannot be chosen (Stripe vs Merchant of Record vs regional) | WP-P0-15, Phase 10 |
| B-03 | **Pilot customer**: who, contact person, timing (open question 14) | Phase 0 exit; P1.18 at the latest | M0 and M1 gates cannot pass; no golden datasets; no UAT | WP-P0-05, WP-P0-07, WP-P0-20 |
| B-04 | **Budget and team for P1–P3** (people, roles, time) | Phase 0 exit gate | Agent-only pace; the two-reviewer rule stays owner-only | [Team & governance](team-and-governance.md), M0 gate |
| B-05 | **On-prem or dedicated hosting** required by any customer? (open question 10) | Before P1.17 | Only shared SaaS is planned | ADR-0012 (Enterprise realm), infrastructure |
| B-06 | **Logo and brand colours** of Karry | P1.7 (placeholders in use) | Placeholder logo and token colours stay; tenant branding overrides them anyway | `@karry/ui` tokens, Keycloak theme |
| B-07 | **Brand and trademark check** for the name "Karry" (WP-P0-19) | Before public launch (Phase 10), ideally now | Risk of renaming after launch | [Legal brief Q13](../01-product/discovery/legal-questions-for-counsel.md) |

## 3. Accounts, infrastructure and providers

| # | What is wanted | Needed by | If late |
|---|---|---|---|
| N-07 | **Hosting provider and region** (WP-P0-14), with a **cloud account with billing** | **P1.17** | No dev/staging environments, no KMS, no cloud WORM storage; M1 cannot pass |
| N-08 | **Domain name** (for example `karry.app`) and DNS access | P1.17 (also N-02) | No public URLs or tenant subdomains, no TLS certificates |
| N-09 | **Managed PostgreSQL check**: the chosen provider must offer PostGIS, TimescaleDB, pgvector and pgcrypto (WP-P0-13) | With N-07 | Self-hosted PostgreSQL on VMs (more operations work) |
| N-10 | **Error tracking**: Sentry SaaS account or self-hosted | P1.17 | The SDK runs with a no-op DSN; production errors are only in logs |
| N-11 | **Billing provider** (WP-P0-15): Stripe, Paddle / Lemon Squeezy (Merchant of Record) or a regional one; payment methods (card, SEPA, mobile money) | Phase 10 (checkout); choice earlier helps P1.10 | Subscriptions stay managed by Karry staff; no self-service payment |
| N-12 | **Tax jurisdictions** to support at launch (VAT/TVA rules, tax ID formats) | Phase 10 | Invoices only in the pilot country |
| N-13 | **GitHub plan**: decided **no paid plan** (2026-10-22); the merge guard is the permanent merge path. Tell the agent if this changes | Informational | none |
| N-14 | **Map tile provider** for the portal map picker (MapTiler, Stadia Maps or self-hosted tiles; ADR-0024): account and style URL for `VITE_MAP_STYLE_URL` | P1.17 (production) | Development uses MapLibre's public demo style; latitude and longitude fields always work |

## 4. Data, reviews and sign-offs

| # | What is wanted | Needed by | If late |
|---|---|---|---|
| D-01 | **Formula handbook expert sign-off**, units section first (WP-P0-04; 22 golden examples) | P1.12 | Code follows the documented formulas; golden values flagged "pending sign-off" |
| D-02 | **Shift patterns and holiday calendars** of target customers (open question 7: 2×12 h, 3×8 h, variable?) | P1.11 | Configurable defaults |
| D-03 | **Review of seeded machine types and default meters** | P1.13 | Catalogue defaults |
| D-04 | **An anonymised real asset list** from a prospect | P1.14 (import) | Synthetic test files only |
| D-05 | **Pilot master data** (sites, assets, materials, people) for the assisted import session (UAT) | P1.18 | M1 cannot pass |
| D-06 | **Demo attendance** and **Sev-1 / Sev-2 triage decisions** during the pilot | P1.18 | M1 cannot pass |
| D-07 | **Golden datasets** from the pilot candidate (WP-P0-05) | Phase 0 exit; Phase 2 | Tests use synthetic data |
| D-08 | **Legal review** with counsel (WP-P0-06), including **legal retention periods** for tickets, waybills and financial records (open question 11) | Before pilot data is stored in production | Retention defaults may be wrong for the country |

## 5. Hardware

| # | What is wanted | Needed by | If late |
|---|---|---|---|
| H-01 | **A real low-end Android tablet** (2 GB RAM, Android 10–12) for device tests; also completes the Phase 0 device-lab checklist (WP-P0-10) | P1.15 | Desktop Chromium only; device acceptance criteria stay open |
| H-02 | **Weighbridge indicator brands and models** at the pilot site (open question 3), ideally access to one indicator (or its protocol manual) | Phase 3 (weighbridge), Phase 6 (edge agent) | Only the serial simulator; the first 3 protocols are guessed |
| H-03 | **Edge PC hardware** for the weighbridge / plant (and who installs it on site) | Phase 6 | Hardware-in-the-loop tests only at the pilot |
| H-04 | **OEM telematics** on pilot machines? Which brands (open question 4) | Phase 9 (integrations) | Manual meter readings only |

## 6. Domain questions (answer from interviews and site visits)

From the [hand-off §7](../00-llm/llm_hands_off.md#7-open-questions-awaiting-the-product-owner) (numbers kept):

| # | Question | Needed by |
|---|---|---|
| Q2 | Connectivity at target sites (none / intermittent 3G-4G / office Wi-Fi only) | P1.15 (offline sizing) |
| Q5 | How revenue is attributed per machine today; which policy the CFO accepts | Phase 7 (finance) |
| Q6 | Invoicing basis per customer (tonnes vs m³); who sets densities and how often | P1.12 (units), Phase 3 |
| Q8 | Who approves today, and how many entries per controller per shift | Phase 2 (verification) |
| Q9 | Most expensive wear parts and their real lead times | Phase 4 (maintenance), Phase 5 (inventory) |
| Q12 | ERP / accounting integration needed for the pilot? Which system | Phase 9; earlier if the pilot requires it |

(Q1, Q3, Q4, Q7, Q10, Q11, Q13 and Q14 are listed above as B-01, H-02, H-04, D-02, B-05, D-08, B-02 and B-03.)

## 7. Phase 0 discovery work packages (owner work)

| WP | Work package | Status | Kit |
|---|---|---|---|
| WP-P0-01 | Customer discovery interviews (≥ 8) | ⏳ | [interview-guide.md](../01-product/discovery/interview-guide.md) |
| WP-P0-02 | Site visits (≥ 2, including a shift change) | ⏳ | [site-visit-checklist.md](../01-product/discovery/site-visit-checklist.md) |
| WP-P0-03 | Event storming with a domain expert (needs WP-P0-02) | ⬜ | [bounded-contexts.md](../03-domain/bounded-contexts.md) |
| WP-P0-07 | Pricing validation (≥ 5 prospects) and a **signed pilot LOI** | ⏳ | [pricing-validation.md](../01-product/discovery/pricing-validation.md) |
| WP-P0-08 | Validate personas and journeys with real people | ⬜ | [personas-and-use-cases.md](../01-product/personas-and-use-cases.md) |
| WP-P0-09 | Hi-fi clickable prototype and usability test (UX) | ⬜ | Spike PWA as a basis |
| WP-P0-20 | Pilot success metrics agreed with the pilot customer | ⏳ | [pilot-charter-template.md](../01-product/discovery/pilot-charter-template.md) |

Also WP-P0-04 (D-01), WP-P0-05 (D-07), WP-P0-06 (D-08), WP-P0-13 (N-09), WP-P0-14 (N-07), WP-P0-15 (N-11) and WP-P0-19 (B-07) above.
Status lives in the [Phase 0 tracker](../04-phases/phase-00-discovery/tracker.md).

## 8. Later phases (🟡, decide when the phase comes closer)

| Phase | What will be wanted | Plan |
|---|---|---|
| P3 Weighbridge & fuel pilot | SMS/WhatsApp provider live with credentials in the vault (N-03); per-plan monthly SMS quota (N-01) | [Phase 3](../04-phases/phase-03-weighbridge-fuel-pilot/plan.md) |
| P6 Plant & edge | Indicator protocols to support first (H-02); hardware bundle and partner installers in remote sites (H-03); installation services offer and price | [Phase 6](../04-phases/phase-06-plant-production-edge/plan.md) |
| P7 Finance & weather | FX rate source (manual table or a provider feed); functional currency per tenant; weather provider (Open-Meteo by default, OK?) | [Phase 7](../04-phases/phase-07-finance-weather-analytics/plan.md) |
| P8 AI copilot | AI provider(s) allowed (external vs sovereign / EU-hosted), provider account and keys, monthly AI budget per plan | [Phase 8](../04-phases/phase-08-ai-copilot/plan.md) |
| P9 Integrations | ERP / accounting systems to connect (Q12), telematics brands (H-04) | [Phase 9](../04-phases/phase-09-integrations-api/plan.md) |
| P10 SaaS GA | Billing provider (N-11), tax jurisdictions (N-12), payment methods (card, SEPA, mobile money), referral programme rules, terms of service and privacy policy from counsel | [Phase 10](../04-phases/phase-10-saas-ga/plan.md) |

## 9. Answered (history)

| Item | Answer | Date |
|---|---|---|
| Docker access for the agent | Given (user in `docker` group) | 2026-10-22 |
| PR merge rule | D29: agent merges normal PRs on green; owner-approval areas need the owner | 2026-10-22 |
| GitHub branch protection / paid plan | No paid plan; `tools/ci/merge_guard.py` is permanent (D32) | 2026-10-22 |
| ADRs 0001–0021 | Accepted by the owner | 2026-10-22 – 2026-10-24 |
| GitHub Actions billing | Repository made public; Actions run free | 2026-10-24 |
| Merging owner-approval PRs | Owner lets the agent merge green PRs itself through the merge guard (D38) | 2026-10-25 |
| ADR-0022 (authorization engine) | Accepted with the P1.9 PRs #50–#55 | 2026-10-25 |

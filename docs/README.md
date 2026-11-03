# Karry Platform — Documentation

> **Start here.** This is the map of all project documentation. It is organized **by area**, with **one topic per file**, so humans and AI agents can find the right file quickly and load only what a task needs.
>
> **AI agents:** read [`00-llm/llm_hands_off.md`](00-llm/llm_hands_off.md) and [`00-llm/llm_guidelines.md`](00-llm/llm_guidelines.md) first, then use the [lookup table](#where-do-i-find) below.

## Folder map

| Folder | What lives here | Index |
|---|---|---|
| `00-llm/` | Rules and memory for AI coding agents: engineering guidelines, hand-off log | [README](00-llm/README.md) |
| `01-product/` | Business model & pricing, personas & use cases, go-to-market, onboarding playbook, KPIs | [README](01-product/README.md) |
| `02-architecture/` | Architecture overview, principles, stack, C4 views, modular-monolith decision, data, offline sync, trust layer, security, AI, analytics, integrations, API standards, NFRs, repo layout | [README](02-architecture/README.md) |
| `03-domain/` | Business logic: spec corrections, bounded contexts, formulas & engines, glossary, catalogues (roles, detection rules, notifications, documents) | [README](03-domain/README.md) |
| `04-phases/` | Roadmap, conventions, story index, traceability, and **one folder per phase** (plan, later test reports and exit reviews) | [README](04-phases/README.md) |
| `05-quality/` | Testing strategy, delivery framework (DoR/DoD), cross-cutting acceptance criteria, defect severity, test environments & devices, translation review, UI/UX review | [README](05-quality/README.md) |
| `06-operations/` | Infrastructure, DevOps & observability, release management, runbooks | [README](06-operations/README.md) |
| `07-management/` | Team & governance, risk register, open questions, everything still wanted from the team | [README](07-management/README.md) |
| `08-user-help/` | End-user help articles in French and English (from Phase 1) | [README](08-user-help/README.md) |
| `adr/` | Architecture Decision Records (one file per decision) | [README](adr/README.md) |

## Where do I find…?

| I need… | Go to |
|---|---|
| What has been decided, mistakes to avoid, current state, next steps | [00-llm/llm_hands_off.md](00-llm/llm_hands_off.md) |
| Everything the owner or team still has to give or decide (providers, accounts, prices, hardware, reviews) | [07-management/still_wanted_from_team.md](07-management/still_wanted_from_team.md) |
| How to work: testing, CI, security, git, Definition of Done | [00-llm/llm_guidelines.md](00-llm/llm_guidelines.md) |
| **Current phase status** (what is done, what is waiting on the owner) | [04-phases/phase-00-discovery/tracker.md](04-phases/phase-00-discovery/tracker.md) (Phase 0), [04-phases/phase-01-platform-foundation/tracker.md](04-phases/phase-01-platform-foundation/tracker.md) (Phase 1) |
| **Phase 1 step-by-step execution plan** (sub-phases P1.0 – P1.18: order, inputs, tests, done criteria) | [04-phases/phase-01-platform-foundation/sub-phases.md](04-phases/phase-01-platform-foundation/sub-phases.md) |
| Technical spike results (RLS, offline, device keys, extensions) | [../spikes/README.md](../spikes/README.md) |
| **What is a technical spike?** Phase 0 spikes explained in simple English | [04-phases/phase-00-discovery/spikes-explained.md](04-phases/phase-00-discovery/spikes-explained.md) |
| Golden examples / expected values for formula tests | [03-domain/formulas-worked-examples.md](03-domain/formulas-worked-examples.md) |
| Threats and mitigations | [02-architecture/threat-model.md](02-architecture/threat-model.md) |
| Interview guide, site visit, pricing, pilot charter, legal brief | [01-product/discovery/](01-product/discovery/README.md) |
| The big picture in 5 minutes | [02-architecture/overview.md](02-architecture/overview.md) |
| Plans, prices, subscription lifecycle, entitlements | [01-product/business-model-and-pricing.md](01-product/business-model-and-pricing.md) |
| Why a modular monolith and not microservices | [02-architecture/modular-monolith-decision.md](02-architecture/modular-monolith-decision.md) |
| Which libraries / versions / tools to use | [02-architecture/technology-stack.md](02-architecture/technology-stack.md) |
| Module boundaries, aggregates, domain events | [03-domain/bounded-contexts.md](03-domain/bounded-contexts.md) |
| A formula (units, blast, mass balance, RUL, fuel, DSCR, weather, stock variance) | [03-domain/computational-engines.md](03-domain/computational-engines.md) |
| Why the original spec was changed | [03-domain/spec-review-corrections.md](03-domain/spec-review-corrections.md) |
| A French/English domain term | [03-domain/glossary.md](03-domain/glossary.md) |
| Fraud/alert rules and thresholds (`RULE-…`) | [03-domain/catalogues/detection-rules.md](03-domain/catalogues/detection-rules.md) |
| Who can do what (roles, masking) | [03-domain/catalogues/roles-and-permissions.md](03-domain/catalogues/roles-and-permissions.md) |
| A user story (`US-P…`) and its acceptance criteria | [04-phases/story-index.md](04-phases/story-index.md), then the phase `plan.md` |
| A test case (`TC-P…`) | The test plan section of the phase `plan.md` (search the ID) |
| What a phase delivers and its exit gate | `04-phases/phase-NN-*/plan.md` ([overview](04-phases/phases-overview.md)) |
| Criteria every story must meet (`CC-…`) | [05-quality/cross-cutting-acceptance-criteria.md](05-quality/cross-cutting-acceptance-criteria.md) |
| Definition of Ready / Done, sprint cadence | [05-quality/delivery-framework.md](05-quality/delivery-framework.md) |
| UI/UX design rules for new screens, usability findings (HCI laws, Nielsen, WCAG) | [05-quality/ui-ux-review.md](05-quality/ui-ux-review.md) |
| Languages of the apps, translation status, Arabic / Malinké / N'Ko review, glossary | [05-quality/translation-review.md](05-quality/translation-review.md), [ADR-0030](adr/0030-arabic-and-malinke-languages.md) |
| Tenancy, RLS, RBAC, threat model | [02-architecture/security-identity-tenancy.md](02-architecture/security-identity-tenancy.md) |
| Signatures, hash-chained ledger, scale ticket rules | [02-architecture/trust-layer-signatures-audit.md](02-architecture/trust-layer-signatures-audit.md) |
| Offline storage & sync protocol | [02-architecture/offline-sync.md](02-architecture/offline-sync.md) |
| Database conventions, tables, retention | [02-architecture/data-architecture.md](02-architecture/data-architecture.md) |
| API conventions | [02-architecture/api-standards.md](02-architecture/api-standards.md) |
| AI copilot design | [02-architecture/ai-copilot-architecture.md](02-architecture/ai-copilot-architecture.md) |
| CI/CD, environments, observability, backup/DR | [06-operations/infrastructure-devops-observability.md](06-operations/infrastructure-devops-observability.md) |
| Release checklist, incidents | [06-operations/release-management.md](06-operations/release-management.md) |
| Risks and open questions for the owner | [07-management/risk-register.md](07-management/risk-register.md), [07-management/open-questions.md](07-management/open-questions.md) |
| Folder layout of the code repository | [02-architecture/repository-structure.md](02-architecture/repository-structure.md) |

## Conventions (mandatory for every new doc)

1. **One topic per file**, placed in the folder of its area. Never add files at the `docs/` root (only this README lives here). Split a file when it grows past ~600 lines.
2. **File names**: `kebab-case.md`, descriptive, no dates or version numbers in the name. Phase work goes in `04-phases/phase-NN-<slug>/` (`plan.md`, `test-report.md`, `exit-review.md`, `retrospective.md`, `sprint-NN.md`). ADRs: `adr/NNNN-short-title.md`.
3. **Front matter** at the top of every doc (agents search it):
   ```yaml
   ---
   title: "Human-readable title"
   area: product | architecture | domain | phases | quality | operations | management | user-help | adr
   tags: [searchable, keywords]
   source: "where the content came from (doc §, ADR, meeting)"
   status: draft | baseline | accepted | superseded
   last_updated: YYYY-MM-DD
   ---
   ```
4. **Breadcrumb** line under the H1, e.g. `> [Docs index](../README.md) › [03-domain](README.md)` (paths relative to the file).
5. **Links**: relative Markdown links to the file and heading anchor. No bare "§N of another doc".
6. **Stable IDs** are the search keys across docs and code: `US-P<n>-<AREA>-<nn>` (stories), `TC-P<n>-<AREA>-<nnn>` (test cases), `CC-nn` (cross-cutting criteria), `RULE-<AREA>-<nn>` (detection rules), `EP-P<n>-<nn>` (epics), `WP-P0-<nn>` (work packages), `D<n>` (settled decisions in the hand-off), `ADR-<nnnn>`. See [04-phases/conventions.md](04-phases/conventions.md).
7. **Keep indexes current**: when you add, move or rename a file, update the folder README, the lookup table above, and all links.
8. **Validate**: `python3 tools/docs/check_links.py` must report `0 broken link(s)` before committing.
9. **Diagrams**: Mermaid, with labels quoted when they contain punctuation (`A["get(x)"]`).

## Search tips for agents

- Find a story or test: `grep -rn "US-P2-FO-04" docs/` or `grep -rn "TC-P3-WB-006" docs/`.
- Find everything about a topic: `grep -rln "tags:.*fuel" docs/`, or grep a domain word from the [glossary](03-domain/glossary.md).
- Open the **folder README first**; each one lists its files with a one-line purpose.

## Legacy section map

The docs were first written as two large files (`MASTER_PLAN.md`, `PHASES_DETAILED_PLAN.md`) and split on 2026-10-21. Old references such as "master plan §10.5" map as follows (subsection numbers are kept inside each file, e.g. §10.5 is heading "10.5" in the computational engines doc):

| Old reference | New location |
|---|---|
| `MASTER_PLAN.md` §1 | [Architecture Overview & Executive Summary](02-architecture/overview.md) |
| `MASTER_PLAN.md` §2 | [Architecture Overview & Executive Summary](02-architecture/overview.md) |
| `MASTER_PLAN.md` §3 | [Specification Review & Corrections](03-domain/spec-review-corrections.md) |
| `MASTER_PLAN.md` §4 | [Product Scope, Personas & Use Cases](01-product/personas-and-use-cases.md) |
| `MASTER_PLAN.md` §5 | [Non-Functional Requirements](02-architecture/non-functional-requirements.md) |
| `MASTER_PLAN.md` §6 | [Architecture Principles](02-architecture/architecture-principles.md) |
| `MASTER_PLAN.md` §7 | [Technology Stack](02-architecture/technology-stack.md) |
| `MASTER_PLAN.md` §8 | [System Architecture (C4)](02-architecture/system-architecture.md) |
| `MASTER_PLAN.md` §9 | [Domain Model & Bounded Contexts](03-domain/bounded-contexts.md) |
| `MASTER_PLAN.md` §10 | [Computational Engines (Corrected Mathematics)](03-domain/computational-engines.md) |
| `MASTER_PLAN.md` §11 | [Data Architecture](02-architecture/data-architecture.md) |
| `MASTER_PLAN.md` §12 | [Offline-First & Synchronization](02-architecture/offline-sync.md) |
| `MASTER_PLAN.md` §13 | [Trust Layer: Verification, Signatures & Audit Ledger](02-architecture/trust-layer-signatures-audit.md) |
| `MASTER_PLAN.md` §14 | [Security, Identity & Multi-Tenancy](02-architecture/security-identity-tenancy.md) |
| `MASTER_PLAN.md` §15 | [AI Copilot Architecture](02-architecture/ai-copilot-architecture.md) |
| `MASTER_PLAN.md` §16 | [Visual Analytics Architecture](02-architecture/visual-analytics.md) |
| `MASTER_PLAN.md` §17 | [Integrations & Edge](02-architecture/integrations-and-edge.md) |
| `MASTER_PLAN.md` §18 | [API Design Standards](02-architecture/api-standards.md) |
| `MASTER_PLAN.md` §19 | [Infrastructure, DevOps & Observability](06-operations/infrastructure-devops-observability.md) |
| `MASTER_PLAN.md` §20 | [Quality Engineering & Testing Strategy](05-quality/testing-strategy.md) |
| `MASTER_PLAN.md` §21 | [Roadmap Summary](04-phases/roadmap-summary.md) |
| `MASTER_PLAN.md` §22 | [Team, Governance & Ways of Working](07-management/team-and-governance.md) |
| `MASTER_PLAN.md` §23 | [Risk Register](07-management/risk-register.md) |
| `MASTER_PLAN.md` §24 | [Architecture Decision Records (ADR) Index](adr/README.md) |
| `MASTER_PLAN.md` §25 | [Requirements Traceability Matrix](04-phases/requirements-traceability.md) |
| `MASTER_PLAN.md` §26 | [Repository Structure](02-architecture/repository-structure.md) |
| `MASTER_PLAN.md` §27 | [Glossary (EN / FR)](03-domain/glossary.md) |
| `MASTER_PLAN.md` §28 | [Open Questions & Next Steps](07-management/open-questions.md) |
| `MASTER_PLAN.md` §29 | [Open Questions & Next Steps](07-management/open-questions.md) |
| `PHASES_DETAILED_PLAN.md` §1 | [Phase Plan Conventions (IDs & Story Template)](04-phases/conventions.md) |
| `PHASES_DETAILED_PLAN.md` §2 | [Business Model, Subscription Plans & Pricing](01-product/business-model-and-pricing.md) |
| `PHASES_DETAILED_PLAN.md` §3 | [Decision: Modular Monolith vs Microservices](02-architecture/modular-monolith-decision.md) |
| `PHASES_DETAILED_PLAN.md` §4 | [Delivery Framework (Cadence, DoR, DoD)](05-quality/delivery-framework.md) |
| `PHASES_DETAILED_PLAN.md` §5 | [Cross-Cutting Acceptance Criteria (CC-01…CC-18)](05-quality/cross-cutting-acceptance-criteria.md) |
| `PHASES_DETAILED_PLAN.md` §6 | [Phases Overview, Timeline & Commercial Packaging](04-phases/phases-overview.md) |
| `PHASES_DETAILED_PLAN.md` §7 | [Phase 0 — Discovery & Architecture Baseline](04-phases/phase-00-discovery/plan.md) |
| `PHASES_DETAILED_PLAN.md` §8 | [Phase 1 — Platform Foundation & SaaS Core](04-phases/phase-01-platform-foundation/plan.md) |
| `PHASES_DETAILED_PLAN.md` §9 | [Phase 2 — Field Operations, Verification & Signatures](04-phases/phase-02-field-operations/plan.md) |
| `PHASES_DETAILED_PLAN.md` §10 | [Phase 3 — Weighbridge, Fuel, Integrity & Pilot](04-phases/phase-03-weighbridge-fuel-pilot/plan.md) |
| `PHASES_DETAILED_PLAN.md` §11 | [Phase 4 — Maintenance & Reliability](04-phases/phase-04-maintenance/plan.md) |
| `PHASES_DETAILED_PLAN.md` §12 | [Phase 5 — Inventory & Multi-Warehouse Logistics](04-phases/phase-05-inventory-warehouses/plan.md) |
| `PHASES_DETAILED_PLAN.md` §13 | [Phase 6 — Plant, Production, Stockpiles & Edge Agent](04-phases/phase-06-plant-production-edge/plan.md) |
| `PHASES_DETAILED_PLAN.md` §14 | [Phase 7 — Asset Finance, Weather & Analytics](04-phases/phase-07-finance-weather-analytics/plan.md) |
| `PHASES_DETAILED_PLAN.md` §15 | [Phase 8 — AI Copilot](04-phases/phase-08-ai-copilot/plan.md) |
| `PHASES_DETAILED_PLAN.md` §16 | [Phase 9 — Integrations, Telematics & Public API](04-phases/phase-09-integrations-api/plan.md) |
| `PHASES_DETAILED_PLAN.md` §17 | [Phase 10 — SaaS General Availability & Growth](04-phases/phase-10-saas-ga/plan.md) |
| `PHASES_DETAILED_PLAN.md` §18 | [Phase 11+ — Continuous Evolution Backlog](04-phases/phase-11-evolution-backlog/plan.md) |
| `PHASES_DETAILED_PLAN.md` §19 | [Go-to-Market & Parallel Commercial Tracks](01-product/go-to-market.md) |
| `PHASES_DETAILED_PLAN.md` §20 | [Pilot & Customer Onboarding Playbook](01-product/customer-onboarding-playbook.md) |
| `PHASES_DETAILED_PLAN.md` §21 | [Release Management & Go-Live Checklists](06-operations/release-management.md) |
| `PHASES_DETAILED_PLAN.md` §22 | [Product & Business KPIs](01-product/kpis.md) |
| `PHASES_DETAILED_PLAN.md` §23 | [Default Role & Permission Matrix](03-domain/catalogues/roles-and-permissions.md) |
| `PHASES_DETAILED_PLAN.md` §24 | [Alert & Detection Rule Catalogue](03-domain/catalogues/detection-rules.md) |
| `PHASES_DETAILED_PLAN.md` §25 | [Notification Catalogue](03-domain/catalogues/notifications.md) |
| `PHASES_DETAILED_PLAN.md` §26 | [Report & Document Catalogue](03-domain/catalogues/documents-and-reports.md) |
| `PHASES_DETAILED_PLAN.md` §27 | [Defect Severity & Triage SLAs](05-quality/defect-severity-and-triage.md) |
| `PHASES_DETAILED_PLAN.md` §28 | [Test Environments, Data & Device Matrix](05-quality/test-environments-and-devices.md) |
| `PHASES_DETAILED_PLAN.md` §29 | [Story Index (all user stories)](04-phases/story-index.md) |

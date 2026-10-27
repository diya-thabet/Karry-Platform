# LLM Hand-Off Log (Project Memory Cache)

> **Purpose.** This file holds the working memory of the Karry project for any LLM or AI coding agent that picks up the work. It records **what has been decided, what has been done, what went wrong, and what to do next**, so the next agent does not repeat mistakes, re-open settled decisions, or redo finished work.
>
> **Every LLM session MUST read this file first and MUST update it before ending** (see §9 for the protocol). The behaviour rules themselves are in [`llm_guidelines.md`](./llm_guidelines.md).

| Field | Value |
|---|---|
| Project | Karry Platform: Enterprise Quarry & Mining Management SaaS |
| Repository | `github.com/diya-thabet/Karry-Platform` |
| Default branch | `main` |
| Current phase | **Phase 1 in progress** (sub-phases P1.0–P1.12 ✅; next P1.13 assets and meters). Phase 0 human work packages continue with the owner. Live status: [Phase 1 tracker](../04-phases/phase-01-platform-foundation/tracker.md), [Phase 0 tracker](../04-phases/phase-00-discovery/tracker.md) |
| Last updated | 2026-10-26 |
| Last updated by | Claude (Opus 5.5) acting as software architect |

---

## Table of Contents

1. [Read Order for a New Session](#1-read-order-for-a-new-session)
2. [Project Snapshot](#2-project-snapshot)
3. [Settled Decisions (do not re-open without an ADR)](#3-settled-decisions-do-not-re-open-without-an-adr)
4. [Known Mistakes & Corrections (do not repeat)](#4-known-mistakes--corrections-do-not-repeat)
5. [Traps, Gotchas & Environment Notes](#5-traps-gotchas--environment-notes)
6. [Current State of the Repository](#6-current-state-of-the-repository)
7. [Open Questions Awaiting the Product Owner](#7-open-questions-awaiting-the-product-owner)
8. [Next Steps (prioritised)](#8-next-steps-prioritised)
9. [Hand-Off Protocol & Entry Template](#9-hand-off-protocol--entry-template)
10. [Session Log (newest first)](#10-session-log-newest-first)

---

## 1. Read Order for a New Session

1. **This file**, completely.
2. [`llm_guidelines.md`](./llm_guidelines.md): how you must work (non-negotiable rules, testing, CI, git).
3. [`docs/README.md`](../README.md): docs map + **"where do I find…" lookup table**. Use it to open only the files your task needs.
4. The relevant area docs: architecture [`02-architecture/`](../02-architecture/README.md), domain & formulas [`03-domain/`](../03-domain/README.md), the current phase plan in [`04-phases/`](../04-phases/README.md) (user stories `US-…`, acceptance criteria, test cases `TC-…`, exit gates), business model in [`01-product/`](../01-product/README.md).
5. `docs/adr/` (when it exists): accepted architecture decisions.
6. `git log --oneline -30` and open PRs/issues, to see what changed since this file was last updated.
7. If this file and the code disagree, **the code and git history are the truth**. Fix this file.

---

## 2. Project Snapshot

- **What:** Multi-tenant SaaS (monthly/annual subscription), offline-first PWA, for quarries, crushing plants, sand washing and open-pit mines.
- **Core value:** trusted field data (anti-fraud on fuel, meters, scale tickets), predictive maintenance, multi-warehouse logistics, plant mass balance, per-machine finance (DSCR), weather-aware planning, AI copilot.
- **Business model:** per-site-per-month plans (Starter / Professional / Business / Enterprise) + add-ons; field users unlimited; see [Business Model, Subscription Plans & Pricing §2](../01-product/business-model-and-pricing.md).
- **Languages:** French + English from day 1 (Arabic RTL-ready later).
- **Owner / product owner:** the repository owner (GitHub `diya-thabet`). The human makes product decisions; the LLM acts as senior architect/engineer/QA/DevOps.

---

## 3. Settled Decisions (do not re-open without an ADR)

| # | Decision | Where documented | Why (one line) |
|---|---|---|---|
| D1 | **Modular monolith**, not microservices, at start; ~4 custom deployables (API, Worker, Python analytics sidecar, Edge agent) + Keycloak | [Decision: Modular Monolith vs Microservices §3](../02-architecture/modular-monolith-decision.md), [Technology Stack §7.2](../02-architecture/technology-stack.md#72-why-a-modular-monolith-not-microservices) | Small team, time-to-revenue, transactional trust guarantees, offline sync coherence, margin |
| D2 | Extraction to services only on measured triggers (telemetry ingestion and AI gateway are the first candidates) | [Decision: Modular Monolith vs Microservices §3.6](../02-architecture/modular-monolith-decision.md#36-extraction-triggers-when-we-will-create-a-microservice) | Avoid premature distribution |
| D3 | Backend **.NET 10 LTS**, ASP.NET Core, EF Core 10 | [Technology Stack §7](../02-architecture/technology-stack.md) | .NET 9 support ends Nov 2026; .NET 10 is supported to Nov 2028 |
| D4 | **PostgreSQL 17 or 18** (final pick in Phase 0 spike) + PostGIS + TimescaleDB + pgvector | [Technology Stack §7](../02-architecture/technology-stack.md), WP-P0-13 | Extension availability on managed provider must be verified |
| D5 | Frontend React 19 + TypeScript strict + Vite + Tailwind v4 + shadcn/ui + TanStack Query/Router; Dexie (IndexedDB) + Workbox | [Technology Stack §7](../02-architecture/technology-stack.md) | Offline-first PWA on low-end Android |
| D6 | Charts: **Apache ECharts** primary, Recharts only for small widgets | [Technology Stack §7](../02-architecture/technology-stack.md), ADR-0014 | Recharts (SVG) slows beyond ~5k points |
| D7 | **Core business math lives in .NET**, Python only for ML/forecasting/anomaly models | [Specification Review & Corrections §3.2](../03-domain/spec-review-corrections.md#32-terminology--legal-corrections) T3 | Single source of truth per rule |
| D8 | Multi-tenancy: pool model with PostgreSQL **RLS** + transaction-local `set_config('app.tenant_id', $1, true)` (≡ `SET LOCAL`, parameterized) + EF global filters; silo option for Enterprise | [Security, Identity & Multi-Tenancy §14.4](../02-architecture/security-identity-tenancy.md#144-multi-tenancy-implementation) | Defense in depth, low cost per tenant |
| D9 | Identity: **Keycloak** (OIDC + PKCE, MFA for privileged roles); in-app RBAC + ABAC (site/warehouse scope) + field-level masking applied in API serialization | [Security, Identity & Multi-Tenancy §14](../02-architecture/security-identity-tenancy.md) | Standards, enterprise SSO later |
| D10 | Signatures = handwritten strokes (evidence) **+** device ECDSA P-256 (WebCrypto, non-extractable) **+** server KMS countersignature, optional RFC 3161 timestamp | [Trust Layer: Verification, Signatures & Audit Ledger §13.2](../02-architecture/trust-layer-signatures-audit.md#132-signature-envelope) | Real cryptography, not "canvas + hash" |
| D11 | Audit = append-only, hash-chained ledger per tenant, written in the same transaction as the business change, anchored hourly to WORM object storage | [Trust Layer: Verification, Signatures & Audit Ledger §13.3](../02-architecture/trust-layer-signatures-audit.md#133-hash-chained-ledger) | Tamper-evident |
| D12 | **Immutable facts**: signed records are never edited; corrections = reversal + new version linked to original | [Architecture Principles §6](../02-architecture/architecture-principles.md), [Trust Layer: Verification, Signatures & Audit Ledger §13](../02-architecture/trust-layer-signatures-audit.md) | Trust is the product |
| D13 | Every volume carries a **material state** (BANK / LOOSE / COMPACTED); weighbridge mass is authoritative for invoicing | [Specification Review & Corrections §3.1](../03-domain/spec-review-corrections.md#31-mathematical--physical-model-corrections) M2, [Computational Engines (Corrected Mathematics) §10.1](../03-domain/computational-engines.md#101-units--conversion-engine) | Swell factor errors misprice loads by 30–60 % |
| D14 | Solvency = **DSCR** (net cash margin / debt service), not the codex formula | [Specification Review & Corrections §3.1](../03-domain/spec-review-corrections.md#31-mathematical--physical-model-corrections) M7 | Codex formula was dimensionally wrong |
| D15 | AI never computes numbers: **tool-calling over a governed semantic layer**, no free-form text-to-SQL; provider-agnostic gateway; Claude default (`claude-sonnet-5-5` Q&A, `claude-haiku-5-5` routing, `claude-opus-5-5` deep analysis); sovereign self-hosted option | [AI Copilot Architecture §15](../02-architecture/ai-copilot-architecture.md), [Phase 8 — AI Copilot §15](../04-phases/phase-08-ai-copilot/plan.md) | Prevent hallucinated numbers & data leaks |
| D16 | **Plans/entitlements/usage meters built in Phase 1**, not at GA | [Business Model, Subscription Plans & Pricing §2.4](../01-product/business-model-and-pricing.md#24-pricing-must-be-technically-enforceable--entitlements-engine), EP-P1-05 | Needed to sell from Phase 3 |
| D17 | **Field capture is never blocked by billing state** (Restricted state keeps field capture working) | [Business Model, Subscription Plans & Pricing §2.5](../01-product/business-model-and-pricing.md#25-tenant-subscription-lifecycle) | Losing shift data destroys trust |
| D18 | Field users are **unlimited** on all plans; we charge per site + assets + office seats | [Business Model, Subscription Plans & Pricing §2.3](../01-product/business-model-and-pricing.md#23-subscription-plans-illustrative-validated-in-phase-0-with-real-prospects) | Per-operator pricing causes shared logins, which kills the anti-fraud value |
| D19 | Money = `decimal`/`numeric(19,4)` + ISO-4217 currency; never floating point | [Specification Review & Corrections §3.1](../03-domain/spec-review-corrections.md#31-mathematical--physical-model-corrections) M11 | Financial correctness |
| D20 | IDs = UUIDv7 (client-generatable offline) | [Data Architecture §11.2](../02-architecture/data-architecture.md#112-key-conventions) | Offline creation + index locality |
| D21 | Trunk-based development, feature flags, server supports N-2 PWA client versions | [Delivery Framework (Cadence, DoR, DoD) §4.7](../05-quality/delivery-framework.md#47-branching-environments--releases) | Offline devices update late |
| D22 | Avoid libraries that switched to commercial licences (MediatR, AutoMapper, MassTransit v9) unless budgeted; check licences of QuestPDF and others | [Technology Stack §7.1](../02-architecture/technology-stack.md#71-stack-overview), Risk R12 | Licence cost/legal risk |
| D25 | **No AI attribution in git**: no `Co-Authored-By: Claude`/AI trailers or "Generated with" lines in commits or PRs; the owner is sole author | [llm_guidelines.md §20.2](llm_guidelines.md#202-commits) | Owner request 2026-10-21 (GitHub showed "diya-thabet and claude") |
| D26 | Tenant context set with `set_config('app.tenant_id', $1, true)` **batched with the first statement**; RLS policies use `nullif(current_setting('app.tenant_id', true), '')::uuid`; server verifies device signatures as **IEEE P1363** | [rls-pooling](../../spikes/rls-pooling/README.md), [signature-interop](../../spikes/signature-interop/README.md), ADR-0004/0007 | Proven by Phase 0 spikes (leak reproduced with session SET; `''::uuid` error; DER mismatch) |
| D27 | Stack choices fixed in P1.0 (Proposed ADRs): Docker Compose local stack + **SeaweedFS** S3 with Object Lock (MinIO OSS archived), in-house outbox dispatcher + **Quartz.NET**, **OpenFeature** with DB provider, **Node 24 LTS + pnpm 12 + TypeScript 6.0** (typescript-eslint does not support TS 7) | ADR-0017 … 0020, [technology stack §7.1.1](../02-architecture/technology-stack.md#711-verified-versions-phase-1-start-2026-10-22) | Registry check 2026-10-22 |
| D28 | Phase 1 runs **sub-phase by sub-phase** (P1.0 – P1.18); the owner starts each one | [sub-phases.md](../04-phases/phase-01-platform-foundation/sub-phases.md) | Owner request 2026-10-22 |
| D29 | **Merge rule:** agent squash-merges normal PRs when all required checks are green; PRs touching Tenancy, Identity, Trust, Billing, Sync, ledger migrations or CI/CD security settings need the owner's explicit approval | [sub-phases §2.3](../04-phases/phase-01-platform-foundation/sub-phases.md#23-review--merge) | Owner decision 2026-10-22 |
| D30 | **ADRs 0001–0020 accepted** by the owner; changes need a superseding ADR | [ADR index](../adr/README.md) | Owner decision 2026-10-22 |
| D31 | Security scanning = gitleaks + Trivy + Semgrep CE (CodeQL unlicensed on private repo); npm packages ≥ 7 days old (pnpm `minimumReleaseAge`, Renovate); merges only via `tools/ci/merge_guard.py` (see D32) | ADR-0021 (**accepted** by the owner 2026-10-23), [sub-phases §2.3](../04-phases/phase-01-platform-foundation/sub-phases.md#23-review--merge) | P1.2, 2026-10-22 |
| D32 | **No paid GitHub plan**: no server-side branch protection; `tools/ci/merge_guard.py` is the permanent merge path. **After every change, sweep and update every affected doc** (owner instruction) | [guidelines §9.1](llm_guidelines.md#91-pipeline-structure-built-in-p11p12-us-p1-plt-03), §20.1 | Owner, 2026-10-22 |
| D33 | **Telemetry is allow-listed** (span attributes outside `TelemetryAttributes.Allowed` are dropped before export; no URLs, user agents or client IPs; request-start logs not exported). **Observability is code**: dashboards generated by `deploy/observability/dashboards/generate.py` (CI fails if stale), alert rules unit-tested with the promtool of the same LGTM image, one runbook per alert | [infrastructure-devops-observability §19.3](../06-operations/infrastructure-devops-observability.md#193-observability), threat model T-18 | P1.4, 2026-10-23 |
| D34 | **Tenant isolation as built (P1.5):** roles from `deploy/database/roles.sql`; migrations only via `Karry.Host migrate` as `karry_owner`; tenant only from exactly one `tenant_id` claim of a validated token; module endpoints require auth + tenant **by default** (explicit opt-outs); `SET LOCAL` prefixed outside transactions / once per transaction inside, every SaveChanges transactional; RLS via `EnableTenantRls`; tables without tenant RLS only in `deploy/database/global-reference-tables.txt` with a reason; API contract committed (`src/backend/Karry.Host/openapi/v1.json`), oasdiff and the isolation suite are required checks; type rules by reflection (no ArchUnitNET) | [security §14.4.1](../02-architecture/security-identity-tenancy.md#1441-as-implemented-p15-karrybuildingblockstenancy), [ADR-0004 notes](../adr/0004-multi-tenancy-rls.md#implementation-notes-p15-2026-10-23) | P1.5, 2026-10-23 |
| D35 | **Audit ledger as built (P1.6):** RFC 8785 strict canonical JSON (exact decimals as strings); per-tenant chain in `audit.ledger_entries` with the hash spec of `LedgerHash` (newline-joined fields, microsecond UTC), written by `ModuleDbContext` in the business transaction (tenant events without a ledger writer are refused); head-row lock; triggers + revoked privileges; hourly signed anchors in a versioned Object Lock bucket, verifier trusts the **oldest** version; `karry-ledger verify`; nightly Stryker on Trust (break < 75 %) | [trust layer §13.3.1](../02-architecture/trust-layer-signatures-audit.md#1331-as-implemented-p16-trust-module-schema-audit), [ADR-0008 notes](../adr/0008-hash-chained-ledger.md#implementation-notes-p16-2026-10-23) | P1.6, 2026-10-23 |
| D39 | **Plans, entitlements and flags as built (P1.10):** plans are immutable versions (global `billing.plans`); one subscription per tenant (RLS) with the closed state machine of business model §2.5 run by an hourly job; manual invoices; 30-day Professional trial on provisioning; entitlements = plan version + add-ons + overrides + state, cached 60 s and forgotten on change; `.RequiresFeature` → 403 `billing.feature_not_in_plan`; soft limits (limit + 10 % rounded up, `ILimitGuard` → 403 `billing.limit_reached`, never field capture) with a daily snapshot and alerts; subscription state enforced by endpoint kind (403 `billing.subscription_*`); middleware order permission → entitlement → subscription state; feature flags declared in code, OpenFeature .NET and web providers, user → company → platform → environment → default, overrides ledgered, platform values with append-only history | [ADR-0023](../adr/0023-plans-subscriptions-entitlements.md), [ADR-0019](../adr/0019-feature-flags.md), [api-standards](../02-architecture/api-standards.md) | P1.10, 2026-10-25 |
| D41 | **Units and materials as built (P1.12):** units engine in `Karry.SharedKernel.Units` (decimal `Quantity` with m³ always in a state, mass units exact, litres and days never convert, conversion through mass with the factors used, `{value, unit, state}` lower-case on the wire); `Materials` module of its own (codes fixed, archive instead of delete, read for every role, manage privileged); swell derived (ρ_bank / ρ_loose − 1), never stored; density versions by date on the company's clock, only after today except the first; conversions only by the API; `QuantityInput` toggles unit and state and shows the factors on tap; demo materials in `seed-demo` | [ADR-0025](../adr/0025-units-engine-and-materials.md), [api-standards](../02-architecture/api-standards.md) | P1.12, 2026-10-26 |
| D40 | **Organization as built (P1.11):** company settings in Tenancy (ledgered, visible without signing in again, logo public for the sign-in page); `Organization` module with sites as PostGIS geometry (pin, optional boundary of one simple ring ≤ 200 km², checked by NetTopologySuite and `ST_IsValid`), archive instead of delete, the `sites` meter against the sites bought; shift patterns as effective-dated versions (only after the site's today, except the first), non-working days, shift time on the site's clock with NodaTime (night shifts belong to their start date; spring gap forward, autumn overlap earlier offset), 30 days of instances stored in the change's transaction plus a daily job, up to 366 days computed; portal map picker MapLibre GL JS (style URL configurable; provider N-14); demo sites in `seed-demo` | [ADR-0024](../adr/0024-sites-geometry-and-shift-time.md), [api-standards](../02-architecture/api-standards.md) | P1.11, 2026-10-26 |
| D38 | **Authorization as built (P1.9):** permissions and sensitive fields declared by each module in code (`AuthorizationCatalog`, start-up refuses bad declarations); fail-closed `PermissionMiddleware` (undeclared endpoint → 403); scopes per role assignment (tenant / site / warehouse), out of scope → 404; addressed resource checked before body validation; 12 default roles fixed by the catalogue (clone to change), custom roles `custom-<n>`, field levels a per-tenant policy; masking in the JSON contract (`maskedFields`, `x-karry-sensitive`, `SensitiveExport`); step-up 401 `auth.step_up_required` + RFC 9470 `max_age=300`, decided by the handler, portal pop-up and one retry; anti-escalation; ledger before/after; Keycloak `mfa-required` follows the roles; access cached 30 s with per-tenant generation; `GET /me/access` for navigation only. The owner lets the agent merge green PRs (owner-approval areas included) via the merge guard `--owner-approved` | [ADR-0022](../adr/0022-authorization-engine.md), [role matrix](../03-domain/catalogues/roles-and-permissions.md), [api-standards](../02-architecture/api-standards.md) | P1.9, 2026-10-25 |
| D37 | **Identity as built (P1.8):** one Keycloak realm `karry` with Organizations (one per tenant); the tenant claim comes from an admin-only `tenant_id` user attribute (one account per e-mail across tenants); realm and theme are code (`deploy/keycloak/generate_realm.py`, CI `--check`); MFA for privileged roles enforced in Keycloak via role `mfa-required`; Karry users in `identity.users` (field users: employee ID + PBKDF2 PIN); invitations 7-day single-use, token only in the link fragment; deactivation refused by `IAccessRevocation` (≤ 30 s) then Keycloak disable + logout; identity ledger events carry IDs only; apps keep tokens in memory (`@karry/auth`), browser E2E in the local-stack job | [security §14.1.1](../02-architecture/security-identity-tenancy.md#1411-as-implemented-p18-deploykeycloak), [ADR-0012](../adr/0012-identity-authorization.md), [deploy/keycloak](../../deploy/keycloak/README.md) | P1.8, 2026-10-24 |
| D36 | **Frontend as built (P1.7):** `@karry/i18n` (FR/EN ICU, no cross-language fallback, catalog check in CI), `@karry/ui` (tokens are the only colour source; light/dark AA, outdoor AAA; field density ≥ 48 px / ≥ 16 px; shadcn-style on Radix without the CLI; every story × theme checked with axe in Chromium in CI), portal (TanStack Router + Query, one error dialog with the support reference) and field shells, `@karry/api-client` generated from the committed contract, `@karry/observability` (Sentry 11 with all data collection off, hashed tenant) | [sub-phases P1.7](../04-phases/phase-01-platform-foundation/sub-phases.md#p17--frontend-foundations-design-system-i18n-app-shells), tracker | P1.7, 2026-10-23 |
| D24 | **Docs organized by area, one topic per file**, with front matter, folder READMEs, a lookup table in `docs/README.md`, and a link checker (`tools/docs/check_links.py`) that must report 0 broken links | [docs/README.md](../README.md) | Docs will grow; agents must find and load only what they need |
| D23 | Timeline: 8 weeks is NOT realistic; plan is P0–P10 over ~13 months; Starter sellable after P3 | [Phases Overview, Timeline & Commercial Packaging §6](../04-phases/phases-overview.md) | Codex timeline rejected |

---

## 4. Known Mistakes & Corrections (do not repeat)

### 4.1 Errors found in the original specification (the LaTeX "codex")

Do **not** implement these as written in the codex. Use the corrected version.

| Codex item | Mistake | Correct approach |
|---|---|---|
| `V_rock = A × h × cos(θ)` | cos(θ) does not reduce bank volume | `V_bank = B × S × H × N_holes` or surveyed area × H |
| m³ ↔ t toggle | Ignores bank/loose/compacted state | Material state + density per state + swell factor |
| Closed-loop iteration only | No steady state, no particle size distribution | Size-class transfer matrices; `m = (I − Γ)⁻¹ f` |
| Conveyor `Q = Q_nom × φ × ψ × ω` | Factors not measurable | CEMA/ISO 5048 nominal capacity × data-calibrated degradation |
| `δ_abrasion` = Bond index multiplier | Bond Ai is a lab value, not a multiplier | Configurable severity mapping `δ = g(Ai)` |
| Loan solvency `Σ(...) > C_loan_annual / T` | Dimensionally wrong; double-counts depreciation | DSCR |
| Fuel fraud fixed "+18 %" | Many false positives | Per-asset robust baseline (median + MAD) AND 18 % |
| Strict meter continuity → fraud | Ignores meter replacement/reset/maintenance runs | Continuity exception with reason codes + second approval |
| Stock variance ≠ 0 → audit | Impossible for bulk/fluids | Tolerance per category, valued variance |
| "PKI signature = canvas + SHA-256" | Not PKI | See D10 |
| ".NET 9", "PostgreSQL 16" | .NET 9 is STS (EOL Nov 2026) | D3, D4 |
| Background Sync | Not supported on iOS Safari | Sync on foreground/online/timer; Background Sync only as an enhancement |
| RLS example `tenant_id = current_setting(...)` | uuid vs text, unsafe with pooling | `nullif(current_setting('app.tenant_id', true), '')::uuid` + transaction-local `set_config(..., true)` batched with first statement + `FORCE RLS` + non-owner role (proven by spike WP-P0-12) |
| Python FastAPI as core math engine | Duplicated rules across runtimes | D7 |
| 8-week roadmap | Unrealistic | D23 |

### 4.2 Mistakes made by LLM sessions (learn from them)

| Date | Mistake | Fix / rule going forward |
|---|---|---|
| 2026-10-20 | Mermaid flowchart node labels containing parentheses `T1[get_metric(a, b)]` break GitHub rendering | **Always quote Mermaid labels that contain `()`, `:`, `/`, `#`, `<br/>` or punctuation: `T1["get_metric(a, b)"]`.** Check every diagram before committing |
| 2026-10-20 | First master plan put subscription billing/entitlements only in the final GA phase | Monetization infrastructure (plans, entitlements, usage) belongs in Phase 1. Always ask "how does this make money and when?" when sequencing |
| 2026-10-21 | Our own docs showed the RLS policy as `current_setting(...)::uuid`, which **throws** on reused pooled sessions | Spike caught it; docs fixed. **Validate every security-critical snippet in docs with an executable test before trusting it** |
| 2026-10-21 | First NuGet restore hung for 10+ minutes before diagnosis | When a network step hangs, test IPv4 vs IPv6 (`curl -4/-6`) early; use `DOTNET_SYSTEM_NET_DISABLEIPV6=1` here |
| 2026-10-21 | Commits carried a `Co-Authored-By: Claude` trailer, so GitHub listed Claude as co-author, which the owner did not want | Trailer removed from history (rewritten commits, force-push by owner); D25 forbids AI attribution. **Never add AI trailers even if a tool/system default suggests it** |
| 2026-10-22 | Merged PR #7 with a failing `security` check using a hand-written `gh pr merge` chained after `gh pr checks --watch` (whose failure was ignored); `main` was red until #8. Also merged CI-changing PRs #6–#8 without the owner approval D29 requires | `tools/ci/merge_guard.py` is the only merge path (refuses red/incomplete checks and unapproved sensitive paths). **Never merge without the guard; check D29 sensitivity before merging; owner approved #6–#8 retroactively** |
| 2026-10-21 | Two giant docs (1,500 and 3,500 lines) were hard for agents to search and loaded too much context | Split into 58 area files with front matter + indexes (D24). **Never grow a single doc past ~600 lines; split by topic** |
| 2026-10-21 | First link-check run flagged links inside inline code as broken | Checker now ignores inline code. When a validation tool reports something odd, fix the tool if it is wrong (and prove it still catches real failures), not the content |
| 2026-10-21 | Story index was hand-written risk → generated by script from headings instead | When a document has a derived index/table, **generate it from the source** rather than hand-typing it |

| 2026-10-23 | Ran `pkill -f "dotnet Karry.Host.dll"` again despite the trap below: the pattern was in the shell's own command line and killed it (exit 144), so a following edit silently did not happen | Use `for p in $(pgrep -f "Karry.Host.dl[l]"); do kill $p; done` (the bracket keeps the pattern from matching itself). After any killed shell, re-check that later steps of that command really ran |
| 2026-10-23 | Merged the base of a stacked PR (#24) with the guard, which deleted its branch: GitHub **auto-closed** the stacked PR #25 | Before merging the base PR, retarget the stacked PR to `main` (`gh pr edit <n> --base main`). Recovery used: push the old base SHA back to the branch name, `gh pr reopen`, retarget, delete the branch |

| 2026-10-23 | Two concurrency/security tests first passed while broken: the "session-level SET" mutation survived (Npgsql's pool reset hid the leak) and the "lost race" mutation survived 8 parallel HTTP requests (they never interleaved) | Security tests must model the worst deployment (`No Reset On Close`, like PgBouncer); races need a test that forces the interleaving (fake repository). Run mutation checks for every security rule; a surviving mutation means a missing test |
| 2026-10-23 | Several mutation runs "passed" only because the mutated code did not compile (unused `using` → IDE0005) and the old binary was tested | Mutation scripts must report build errors and treat them as invalid runs (done since P1.4) |

| 2026-10-23 | A Python script edited a C# file whose replacement text contained `\ud800` in a non-raw string: Python failed while writing, which **emptied** the (uncommitted) file | Write files that contain escape sequences with the Write tool or with raw strings; check file size after scripted edits of uncommitted files |
| 2026-10-23 | Several lock and race tests first proved nothing (writer "waited" anyway; `ON CONFLICT` waited too; parallel requests never interleaved) and Stryker's first runs scored 70.59 % / 49.06 % on code I had mutation-checked by hand | Freeze the concurrent actor at the exact dangerous step; run Stryker on security-critical modules instead of trusting a handful of manual mutations |
| 2026-10-23 | A merge loop's rebase stopped on uncommitted changes left in the shared working tree by **another agent session**; the guard then tried to merge the stale PR (GitHub refused) | Run `git status` before merge loops; never discard another session's edits: stash them, finish, restore and record them |

| 2026-10-23 | Killed my own shell a third time (`pgrep -f "vite previe[w]"`): the bracket trick fails when the plain text also appears elsewhere in the same command | Never stop processes by pattern from the command that started them: record `$!` or read the PID from `ss -ltnp` and `kill <pid>` |
| 2026-10-23 | `A && B && (cd x && server) &` backgrounded the whole chain, so variables set in it never reached later commands; and `vite preview` listens on IPv6 `localhost` only (connection refused on 127.0.0.1) | Put multi-step background work in a small script file; start dev servers with `--host 127.0.0.1` |

| 2026-10-24 | Three mutation checks reported "not caught" from **stale binaries**: the mutant did not compile (analyzers reject dead code such as `if (false)` or `_ = x;`), the test run used the previous build | Only read a mutation result after `Build succeeded`; write mutants that compile (change a value or a condition, not delete a statement) |
| 2026-10-24 | A dedup mutant survived because the test never re-read an event (the cursor moved past it) | For idempotency, test the replay explicitly (reset the cursor, run again, expect 0 new) |
| 2026-10-26 | `git stash` without `-u` left two new files in the tree; switching branches and `git add -A` committed them into another PR, whose build would have failed (removed by a follow-up commit, since force-pushing was refused) | Stash with `git stash -u`, and check `git show --stat` before pushing |
| 2026-10-27 | A computed property (`DensityVersion.Densities`, carrying the derived swell 2.65 / 1.70 − 1 with 28 digits) was serialized into the ledger event; the canonical JSON refused it and `seed-demo` crashed in `local-stack`. The module tests' densities all gave an exact swell | Mark derived properties on event payload records `[JsonIgnore]`, and test with values whose quotient does not end |
| 2026-10-24 | A first paging design could loop over the same page forever when more events than a page share one millisecond; only a unit test with a fake source showed it | Unit-test paging boundaries (same timestamp > page size) with a fake source before relying on the live test |
| 2026-10-23 | A P1.6 test used a fixed fake date against a real S3 server: it broke the next day | Fake clocks that reach a real server start at the real time (tracker finding 35) |

> Add every new mistake here, including small ones. The next agent benefits most from this section.

---

## 5. Traps, Gotchas & Environment Notes

- **Owner's environment:** Fedora Linux (kernel 7.x), bash, repo at `~/Desktop/Karry-Platform`. Git user `diya-thabet`.
- **Language of the owner:** writes in English (informal); product is bilingual FR/EN. Domain words are often French (*carrière, pontier, magasin général, tapis roulant / sebta, panne compteur, courbes et graphiques*). Keep the glossary in [Glossary (EN / FR) §27](../03-domain/glossary.md) up to date.
- **Owner preference:** wants very detailed, "very pro", enterprise-grade outputs and firm architect opinions with reasoning, not neutral option lists.
- **Git push works** (since 2026-10-22): owner ran `gh auth login` + `gh auth setup-git`; `gh` (`/usr/bin/gh`) is logged in as `diya-thabet` with `repo` + `workflow` scopes. Use `gh run list/watch/view --log-failed` to check CI after every push. Never force-push without the owner's explicit approval.
- **Product code exists since P1.1**: `Karry.slnx` (backend under `src/backend`, tests under `tests/` and inside each module), `src/frontend` (own pnpm workspace), `deploy/docker` (local stack). CI: `ci.yml` (backend, frontend, local-stack), `docs.yml`, `spikes.yml`.
- **.NET SDK** is installed per-user at `~/.dotnet` (10.0.402), **not on PATH**: call `~/.dotnet/dotnet`. Set `DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`.
- **IPv6 is broken on the owner's network**: NuGet restore hangs forever. Always set `DOTNET_SYSTEM_NET_DISABLEIPV6=1` for dotnet restore/build. (curl -4 works; curl -6 times out.)
- **Docker works via `sg docker -c "<command>"`** (owner added the user to the `docker` group on 2026-10-22; the current session has not re-logged in). Run Testcontainers tests as `sg docker -c "DOTNET_SYSTEM_NET_DISABLEIPV6=1 ~/.dotnet/dotnet test --solution Karry.slnx -c Release"`. After a re-login plain `docker` works. Previously: **Docker was not usable by the agent** (`permission denied` on `/var/run/docker.sock`; user not in `docker` group). Testcontainers and the extension check cannot run locally until the owner runs `sudo usermod -aG docker $USER` and re-logs in. Workaround used for PostgreSQL: portable binaries (zonky `embedded-postgres-binaries-linux-amd64` 17.5 from Maven Central) started with `initdb`/`pg_ctl` on TCP `127.0.0.1:55432` in the scratchpad (`-k ''`, because the Unix socket path exceeds 107 bytes). No `psql` in that bundle.
- **.NET 10 + xunit v3**: `dotnet test` requires the Microsoft Testing Platform runner (`global.json` → `"test": {"runner": "Microsoft.Testing.Platform"}`), and the syntax is `dotnet test --solution X.slnx`. Do not add `Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio`. Add `<Using Include="Xunit" />`.
- **Playwright on Fedora** uses the "ubuntu24.04 fallback build" of Chromium, which works. `context.setOffline(true)` does **not** block `context.route` handlers, so fake servers must model outages themselves.
- **MinIO OSS is archived** (2026-04; no community images). Do not add MinIO anywhere; use SeaweedFS (ADR-0017), RustFS as fallback.
- **TypeScript 7 vs lint:** `typescript-eslint` 8.72 supports TypeScript `<6.1.0` only. Product code pins TS 6.0.x (ADR-0020); the Phase 0 spike keeps TS 7 (frozen).
- **Node versions:** Node 24 LTS installed via nvm (`nvm use 24`; the nvm default is still 22). Product repo pins Node 24 via `.nvmrc`/`engines`.
- **actionlint** 1.7.12 is installed at `~/.local/bin/actionlint` (checksum verified). shellcheck is not installed.
- **Version checks that work here:** `npm view <pkg> version`; NuGet flat container `curl -4 -s https://api.nuget.org/v3-flatcontainer/<id>/index.json`; Docker Hub tags API; `gh api repos/<o>/<r>/releases/latest`. Use `curl -4` (IPv6 broken).
- **Local test PostgreSQL without Docker:** portable zonky 17.5 in the scratchpad (`initdb -A scram-sha-256`, `pg_ctl -o "-p 55432 -k '' -c listen_addresses=127.0.0.1"`), then `KARRY_TEST_PG="Host=127.0.0.1;Port=55432;Username=postgres;Password=postgres;Database=postgres"` makes the integration tests use it instead of Testcontainers. No extensions in that build: tests needing PostGIS/Timescale must run in CI until Docker works.
- **Keycloak tests:** `KeycloakServer` starts `quay.io/keycloak/keycloak` with `deploy/keycloak` mounted (~30 s); `KARRY_TEST_KEYCLOAK=http://127.0.0.1:8080` reuses the compose Keycloak. Keycloak marks cookies `Secure` even on loopback HTTP: `CookieContainer` (and Python's cookiejar) silently drop them and the login fails with `cookie_not_found`; `KeycloakBrowser` keeps cookies by hand. The TOTP enrolment page's hidden `totpSecret` is the raw HMAC key (not base32).
- **EF + Npgsql traps (P1.8):** `FromSql("SELECT * …")` on an entity with the `xmin` concurrency token fails (`*` does not return system columns): select `*, xmin`. FluentValidation runs every rule of a chain unless `Cascade(CascadeMode.Stop)`: `NotEmpty().Must(x => x!.Count…)` throws on null and returns 500 (the isolation suite found it). Npgsql reuses transaction objects: never key per-transaction state on the `DbTransaction` instance alone (finding 34).
- **Fake clocks and real servers:** a fixed `FakeTimeProvider` date sent to a real service (S3 retention, Keycloak) becomes a past date the next day; start such clocks at the real time (finding 35).
- **Browser E2E (P1.8):** Playwright's Chromium is `en-US`: the suite sets `locale: "fr-FR"` (the app picks the browser language). Fill Keycloak forms by `#username`, `#password`, `#kc-login` (stable across themes and languages), not by label. The login page's tenant name needs Karry.Host running behind the app origin's `/api` proxy.
- **Keycloak realm is generated:** edit `deploy/keycloak/generate_realm.py`, run it; CI runs `--check`. The test run shares one Keycloak (`KeycloakServer.SharedAsync`).
- **Realm import only runs when the realm is missing:** after editing `deploy/keycloak/realm/karry-realm.json`, the compose Keycloak keeps the old realm until `docker compose … down -v` (or the realm is deleted in the admin console).
- **Frontend locally:** `nvm use 24` then `corepack enable` (pnpm 12 shims) inside `src/frontend`.
- **Analyzers are strict** (`AnalysisLevel=latest-recommended`, code style enforced, warnings = errors): use `LoggerMessage` source-generated logging (CA1848), no constant array arguments (CA1861), `GenerateDocumentationFile` is on for IDE0005. Test naming with underscores is allowed only in test folders (`.editorconfig`).
- **Stacked PRs:** after the base PR is squash-merged, `git rebase --onto origin/main <old-base-tip-sha> <branch>` and `git push --force-with-lease` (own feature branch only). Note the SHA before merging: the guard deletes the merged local branch.
- **Full backend run on this machine: add `--max-parallel-test-modules 4`.** With every test project starting its own PostgreSQL (and Keycloak) container at once, the load average passes 40 on 12 cores and fixtures time out ("Timeout during reading attempt"); with 4 modules at a time the run is green and faster (771 tests in 73 s, 2026-10-26). Test databases are copies of one template per server with `deploy/database/extensions.sql` applied (`PostgresServer`). The Testcontainers server runs with `timescaledb.max_background_workers=0`: TimescaleDB's scheduler otherwise holds a session in the template (copying fails, or the 10 s wait times out: broke `main` CI after #68) and in every test database. Tests that change stored times must not depend on the time of day.
- **xunit v3 single tests:** `dotnet test --project <proj> --filter-method '*Class.Method*'` / `--filter-class '*Class'`.
- **Concurrency tests:** a stress test alone can pass by luck; also write a deterministic test that holds the lock/state in a second connection (see `OutboxTests.HoldMessageAsync`).
- **Merge only with `python3 tools/ci/merge_guard.py <pr>`** (add `--owner-approved` only after the owner explicitly approves a sensitive PR). Never call `gh pr merge` directly: on 2026-10-22 PR #7 was merged with a failing `security` check that way, turning `main` red until #8. Branch protection is unavailable without a paid plan (private repo, HTTP 403) and the owner will not pay: the guard is permanent.
- **`gh pr merge --delete-branch`** also deletes the local branch and switches to `main`; re-create the next branch from `origin/main`.
- **Never `pkill -f <pattern>`** with a pattern that also appears in your own command line: it kills your own shell (exit 144).
- **GitHub Actions:** full product CI is created in Phase 1 (`US-P1-PLT-03`); `docs.yml` and `spikes.yml` exist now. Pin third-party actions by commit SHA (see guidelines §9).
- **xunit v3 TRX reports:** use `--report-xunit-trx`, **not** `--report-trx` (that needs the `Microsoft.Testing.Extensions.TrxReport` package; without it MTP exits with code 5, "Zero tests ran", "Handshake failures"). Always run the exact CI command locally before pushing.
- **pnpm lockfile:** after editing `package.json` versions, run `pnpm install` and commit `pnpm-lock.yaml`; CI uses `--frozen-lockfile` and fails on any specifier mismatch (e.g. `^26.6.5` vs `26.6.5`). Verify with `CI=true pnpm install --frozen-lockfile`.
- **Postgres container readiness:** the image entrypoint runs a temporary socket-only server during init, then restarts. Check readiness with `pg_isready -h 127.0.0.1` (TCP), never via the socket, or the next command races the restart (flaky pg18 check, 2026-10-22).
- **Path-filtered workflows may not trigger on a force-push**; both workflows have `workflow_dispatch`, so run `gh workflow run <file> --ref main`.
- **Versions:** always pin exact versions at the time of implementation and verify they are current and supported. Do not trust remembered version numbers without checking (release notes, `dotnet --list-sdks`, `npm view <pkg> version`).
- **Observability local stack:** Grafana http://localhost:3000; Host/Workers export OTLP to 127.0.0.1:4317 only in Development (`appsettings.Development.json`). Prometheus loads rules only at start: `docker compose … up -d --force-recreate lgtm` after editing rules. Dashboards: edit `generate.py`, run it, commit the JSON.
- **Loki refuses writes above 90 % disk** ("Ingester is shutting down", collector retries silently). CI frees runner disk in `local-stack`; locally check `df` if logs are missing. CI sets `KARRY_LGTM_COMPONENT_LOGS=true` so collector/Loki errors are printed on failure.
- **promtool:** use the one inside the pinned `grafana/otel-lgtm` image (`--entrypoint /otel-lgtm/prometheus/promtool`); test cases need 1-minute samples (Prometheus lookback is 5 min) and one case per protection.
- **Semgrep (CI image) scans only git-tracked files:** `git add` new files before a local scan.
- **Docker pulls over IPv6 fail intermittently here** (connection reset): retry `docker compose pull`.
- **Docker Hub can refuse anonymous pulls in CI** ("unauthorized: authentication required", seen on `main` for #26, 2026-10-23): infrastructure, not code; re-run the failed job (`gh run rerun <id> --failed`). If it recurs, mirror the stack images to GHCR or log in to Docker Hub in CI (decide in P1.17).
- **OpenTelemetry .NET:** OTLP settings (`OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_METRIC_EXPORT_INTERVAL`) are read from `IConfiguration`, so appsettings work. ASP.NET Core puts the full traceparent in problem `traceId` unless overwritten (done in `ApiConventions`).
- **Tenancy in tests:** use `tests/Karry.Testing` (`PostgresServer.NewKarryDatabaseAsync()` = real roles; `TestTokens.TrustTestIssuer()` / `ForTenant` / `ForPlatformOperator`). Module endpoints answer 401/403 without a tenant token; mark public/operator endpoints `.AllowAnonymous()` / `.AllowWithoutTenant()` deliberately and add them to the isolation suite's exemptions with a reason.
- **New table?** Use `migrationBuilder.EnableTenantRls(schema, table)` in its migration, or declare it in `deploy/database/global-reference-tables.txt` with a reason; the linter (Host integration tests) fails otherwise. A new route parameter needs an attack value in `TenantIsolationSuite.AttackValue`.
- **API contract:** building `Karry.Host` regenerates `src/backend/Karry.Host/openapi/v1.json`; commit it (CI fails on drift). Every endpoint needs `.WithName()` and `.ProducesProblem(...)`.
- **Stacked PRs:** retarget the stacked PR to `main` **before** merging its base (the guard deletes the base branch and GitHub would close it); merge with `main`'s copy of `merge_guard.py` when a PR changes the required checks.
- **Local stack:** a stack created before P1.5 lacks `karry_owner`: `docker compose -f deploy/docker/compose.yaml down -v` once.
- **Audit ledger in code:** an aggregate whose events must be audited is `ITenantOwned` or `ITenantAudited`; payload decimals that a double cannot hold exactly make the save fail (strict canonicalization): send such values as strings in events. Ledger tests that tamper need the superuser and `SET session_replication_role = replica` (it disables the triggers).
- **Anchoring locally:** `dotnet run --project src/backend/Karry.Workers -- anchor-now`; the dev signing key is `~/.karry/anchor-signing-dev.pem` (shared by API, workers and CLI); `karry-ledger verify` checks anchors when `Trust__Anchoring__*` is set.
- **Stryker:** `test-runner: mtp` is required (Microsoft Testing Platform); the test project must reference the mutated project directly; local reports with `--output` to the scratchpad (`StrykerOutput/` is ignored).
- **Test databases:** each test creates its own database; `Karry.Testing` caps pools at 20 and clears them on dispose (a full run had exhausted PostgreSQL's 100 connections).
- **Frontend:** `nvm use 24`; pnpm 12 blocks dependency install scripts (decisions in `allowBuilds` in `pnpm-workspace.yaml`) and refuses versions < 7 days old (pick with `npm view <pkg> time`). Accessibility suite: `pnpm --filter @karry/ui exec playwright install chromium` once, then `test:a11y`. After an API contract change: `pnpm --filter @karry/api-client generate`. Every UI string goes through `t("key")` with FR and EN entries (the catalog check and the pseudo-localized render test fail otherwise).
- **Mermaid on GitHub:** see §4.2. Gantt charts render, but keep task IDs simple (`p0`, `p1`).
- **Large docs:** when writing very long Markdown, write in sections/appends and re-verify line count and anchors afterwards.
- **Offline PWA facts:** iOS Safari lacks Background Sync and may evict storage. Always call `navigator.storage.persist()`. Test on real low-end Android.
- **WebCrypto non-extractable keys** survive restarts in IndexedDB but are lost on "clear site data". The re-enrolment flow must exist.
- **EF Core + RLS:** set `app.tenant_id` with `set_config(..., true)` inside the transaction, never at session level (proven leak in TC-P0-RLS-005; connection pooling would leak it).
- **Stacked PRs and squash merges:** after the base PR is squash-merged, the stacked PR conflicts with `main`. Rebase only its own commits: `git rebase --onto origin/main <old base head SHA>`, check that `git diff <old head> <new head>` is empty, then push the feature branch with `--force-with-lease` (never `main`). CI must run again on the new head before `merge_guard.py` accepts it. A PR that changes the required checks (e.g. #52 adds `authorization`) is merged with `main`'s copy of the guard (`git show origin/main:tools/ci/merge_guard.py`).
- **Merging with the branch checked out:** `merge_guard.py` merges, but `gh` cannot delete a branch that is checked out locally (the merge still succeeds). Switch to `main` before merging, or delete the remote branch with `git push origin --delete <branch>`.
- **Quartz cron triggers do not fire at start-up.** `.StartNow()` on a cron trigger only sets when the schedule starts: the first fire is the next cron time (checked 2026-10-25 with Quartz 4.5.1: a cron job with `StartNow()` did not run within 4 s, a separate simple trigger did). Until P1.10 slice 4 the workers' comments claimed "plus once at start-up" while nothing ran, so `KarryLedgerAnchoringStale`'s `absent()` branch could page for up to an hour after a restart. Schedule jobs with `Schedule<TJob>()` in `Karry.Workers/Program.cs` (cron trigger + one-shot start-up trigger).
- **Usage counts are `long`, and OpenAPI turns `long` into `integer | string`.** The generated TypeScript type is `number | string`; convert with `Number()` at the hook (`useUsage`), as the counts are far below 2^53.
- **CI flake: Docker API timeout.** On 2026-10-25 the `backend` job of a docs-only PR (#57) failed because a Testcontainers fixture's Docker request timed out on the runner (`TaskCanceledException` in `Docker.DotNet`), before any test ran. `gh run rerun <id> --failed` passed. Rerun once; if it fails again it is real.
- **GitHub Actions billing:** on 2026-10-24 every job on #51 was refused with "recent account payments have failed or your spending limit needs to be increased" (private repository). No test ran; the merge guard correctly refuses until CI runs again. The owner made the repository **public** the same day (free Actions minutes); refused runs are restarted with `gh run rerun <id>`. Being public, nothing secret may ever be committed (dev/test credentials only).

---

## 6. Current State of the Repository

```
Karry-Platform/
├── CLAUDE.md                     ← pointer for Claude Code (auto-loaded), points to docs below
├── AGENTS.md                     ← pointer for other AI coding agents
├── .gitignore                    ← baseline ignores (.NET, Node, Python, IaC, secrets)
├── .github/workflows/            ← docs.yml (link check), spikes.yml (Phase 0 spike tests)
├── spikes/                       ← Phase 0 spikes with tests: rls-pooling (.NET), signature-interop (.NET), field-capture-pwa (Vite+Playwright), pg-extensions (Docker script)
├── tools/docs/check_links.py     ← validates every relative link & anchor in Markdown
└── docs/                         ← organized by area; see docs/README.md for the full map
    ├── README.md                 ← index, lookup table, conventions, legacy §-reference map
    ├── 00-llm/                   ← llm_guidelines.md, llm_hands_off.md (this file)
    ├── 01-product/ … 07-management/, 08-user-help/, adr/
```

| Phase | Status |
|---|---|
| P0 Discovery | **In progress**: see [tracker](../04-phases/phase-00-discovery/tracker.md) |
| P1 Foundation | **P1.0–P1.12 ✅** (P1.12: #73–#76; P1.11: #66–#71 merged, close-out PR; P1.10: #58–#64, close-out #65; P1.9: #50–#55, close-out #56; owner: Renovate app pending; e-mail sender domain and SMS provider pending; no GitHub Pro → merge guard is permanent): see [Phase 1 tracker](../04-phases/phase-01-platform-foundation/tracker.md) and [sub-phases](../04-phases/phase-01-platform-foundation/sub-phases.md) |
| P2–P11 | Not started |

---

## 7. Open Questions Awaiting the Product Owner

**Everything still wanted from the owner and team (all phases, providers, accounts, data, hardware) is in [still_wanted_from_team.md](../07-management/still_wanted_from_team.md); keep both lists in step.**

Copied from [Open Questions & Next Steps §28](../07-management/open-questions.md#open-questions-for-domain-experts). Remove an item when it is answered and record the answer in [Specification Review & Corrections §3](../03-domain/spec-review-corrections.md) or the relevant doc.

1. Target countries/markets first (drives legal, currency, hosting region, payment provider, languages).
2. Connectivity at target sites (none / intermittent 3G-4G / office Wi-Fi only).
3. Weighbridge indicator brands/models at pilot site.
4. Do pilot machines have OEM telematics?
5. How revenue is attributed per machine today; which policy the CFO accepts.
6. Invoicing basis per customer (tonnes vs m³); who sets densities and how often.
7. Shift patterns (2×12 h? 3×8 h? variable?).
8. Approvers today, and number of entries per controller per shift.
9. Most expensive wear parts and real lead times.
10. Any on-prem/dedicated hosting requirement.
11. Legal retention periods for tickets, waybills, financial records.
12. ERP/accounting integration needed for pilot?
13. Company incorporation country (decides billing provider: Stripe vs Merchant of Record vs regional).
14. Is there a pilot customer identified? Contact and timing.

---

## 8. Next Steps (prioritised)

1. **P1.13 (assets and meters) next**, on the owner's "go". P1.12 is done (D41, ADR-0025 accepted): units engine, `Materials` module, `QuantityInput` and Settings → Materials, demo materials. Golden-example values and the material ranges change only with the domain expert's sign-off (formula handbook, item 6). **Owner inputs:** map tile provider (N-14, before production); typical shift patterns and holiday calendars (open question 7; demo data uses 2×12 h and 3×8 h); final plan limits and prices (N-01). **Local machine:** 14 GB RAM; run targeted test projects locally and leave the full suite to CI (the owner's kind clusters were deleted on 2026-10-26 at their request to free memory). **Follow-up:** platform ledger chain for platform-wide changes (operator console). Carry-overs: PDFs reuse the masking decision (P1.18); the TypeScript RFC 8785 canonicalizer must pass `tests/vectors/rfc8785.json` (P1.15).
**Owner inputs from P1.8:** the e-mail sender domain for invitations (SPF/DKIM; Mailpit locally) and an SMS/WhatsApp provider (phone invitations are previewed in Mailpit until then, refused elsewhere).
2. **Review the competitor analysis** written by another session (`~/Desktop/Karry-Competitor-Analysis.md`, session log below): it recommends a Guinea-first beachhead and deploying at Phase 3 / M2. These are owner decisions, not yet recorded as decisions here.
3. **Phase 1 is executed sub-phase by sub-phase** per [sub-phases.md](../04-phases/phase-01-platform-foundation/sub-phases.md) (P1.0 – P1.18). The owner says "start P1.x"; follow §2 of that file (way of working) and the card. Status in the [Phase 1 tracker](../04-phases/phase-01-platform-foundation/tracker.md). Do not start a sub-phase whose dependencies are not ✅.
4. **Owner item:** install the Renovate GitHub app (only this repository); its first "Configure Renovate" PR touches `renovate.json` and needs owner approval. (P1.0 prerequisites are done: Docker access, merge rule D29, ADRs 0001–0021 accepted.)
5. **Owner Phase 0 work** continues in parallel: interviews, site visits, pricing validation, pilot LOI, legal brief, hosting and billing decisions, answers to §7 (kits in `docs/01-product/discovery/`). Hosting decision blocks P1.17.
6. Formula handbook expert sign-off (P1.12 built on its draft ranges; needed before production).
7. Real-device run of the Phase 0 device-lab checklist (can be combined with P1.15).

---

## 9. Hand-Off Protocol & Entry Template

**At the end of every session (or when context is about to run out), the agent MUST:**

1. Update the header table (current phase, last updated, by whom).
2. Update §3 if a decision was made (and write/link the ADR).
3. Add any mistakes, failed approaches or surprises to §4.2 or §5.
4. Update §6 (repo state & phase status), §7 (questions) and §8 (next steps).
5. Prepend a new entry to §10 using the template below.
6. Commit this file in the same PR/commit as the work it describes.

**Rules for writing entries**
- Facts only. Include file paths, story IDs, test IDs, commit SHAs and PR numbers.
- Record **what failed and why**, not only what succeeded.
- Never state that tests pass unless they were run in this session; include the command used.
- Keep entries short (≤ 25 lines). Long details belong in the docs, ADRs or PR descriptions.

```markdown
### YYYY-MM-DD — <short title> (<agent/model>)
- **Goal:** <what the owner asked>
- **Done:** <what was completed; files; story IDs>
- **Verification:** <commands run and results, e.g. `dotnet test` → 312 passed>
- **Not done / deferred:** <and why>
- **Decisions:** <D-numbers added/changed, ADR links>
- **Mistakes / lessons:** <anything the next agent must avoid>
- **Next:** <the single most important next action>
- **Commits/PR:** <sha / PR #>
```

---

## 10. Session Log (newest first)

### 2026-10-26 — P1.12 materials and units engine (agent)
- **Goal:** owner: "go" (P1.12, slice by slice; merge green PRs myself; keep docs current; keep local runs light).
- **Done:** #73 units engine in SharedKernel (Stryker 91.10 %); #74 `Materials` module with density versions, conversions at a date, company time zone in the Tenancy contract; #75 `QuantityInput` unit/state toggle and factors on tap, portal Settings → Materials; #76 demo materials in `seed-demo` and close-out (ADR-0025 accepted, tracker, sub-phases, D41).
- **Verification:** targeted runs only (low-RAM machine): Materials 38, isolation 69, architecture 30, authorization 6, host integration 18; UI 58, portal 61, browser a11y 82; lint, typecheck, `check:catalogs`, format clean; CI all 10 checks green on every PR. Mutation checks: 4 caught besides Stryker.
- **Not done / deferred:** formula handbook sign-off (ranges are draft); unit preferences per company (`unitSystem` is stored but screens show metric).
- **Decisions:** D41; ADR-0025 accepted.
- **Mistakes / lessons:**
  - axe `label-content-name-mismatch`: a button showing "m³" must have a name containing "m³" (WCAG 2.5.3).
  - A conversion is a POST that stores nothing: the fake API's `writes()` leaves it out.
  - `git stash` without `-u` leaked files into another PR (see 4.2).
- **Next:** P1.13.
- **Commits/PR:** #73 … #76.

### 2026-10-26 — P1.11 organization, sites, shifts and company settings (agent)
- **Goal:** owner: "go" (P1.11, slice by slice; merge green PRs myself; keep docs current).
- **Done:** #66 company settings, logo and branding; #67 Organization module and sites (PostGIS); #68 shift patterns, non-working days, shift instances, daily job and alert; #69 test-server fix (TimescaleDB workers off, time-independent test); #70 portal sites, map picker, shifts, site roles; #71 demo sites with shifts and holidays; close-out: ADR-0024 accepted with an as-built section, tracker and sub-phases, D40. At the owner's request, deleted their kind clusters to free memory.
- **Verification:** backend 812 tests at slice 3 (full run locally with `--max-parallel-test-modules 4`), Organization 58 at slice 5; portal 51; lint, typecheck, `check:catalogs`, `check:generated` clean; promtool rule tests; CI all 10 checks green on every PR, smoke test checks the demo sites. Mutation checks: 26 caught.
- **Not done / deferred:** map tile provider (N-14); movable public holidays are left to each company (demo seeds fixed dates only).
- **Decisions:** D40; ADR-0024 accepted.
- **Mistakes / lessons:**
  - TimescaleDB's scheduler holds sessions on a template database: turn its workers off on test servers.
  - A test that builds instants from "today" broke after 18:00 Tunis time: build test times relative to now.
  - `code` is a reserved problem extension name; MapLibre's types need `@types/geojson` listed in the portal's `types`.
  - The machine has 14 GB RAM: parallel Testcontainers runs make it lag.
- **Next:** P1.12.
- **Commits/PR:** #66 … #71 (d6284df), close-out PR.

### 2026-10-25 — P1.10 plans, entitlements, subscriptions and feature flags (agent)
- **Goal:** owner: "go" (P1.10, slice by slice; merge green PRs myself).
- **Done:** #58 plan catalogue; #59 subscriptions, state machine, invoices, trial; #60 entitlements and `.RequiresFeature`; #61 usage meters, soft limits, snapshot job, alerts, Plan & usage page; #62 seed-demo trial fix; #63 subscription-state enforcement and portal states; #64 feature flags (Flags module, OpenFeature .NET and web); close-out: ADR-0023 accepted with the Billing security review, tracker and sub-phases as built, D39.
- **Verification:** `dotnet test --solution Karry.slnx -c Release` → 708/708; portal 31, all frontend packages green, `check:catalogs` and `check:generated` clean; promtool rule tests; links 0 broken; CI all 10 checks green on every PR. Mutation checks: 17 caught.
- **Not done / deferred:** platform ledger chain for platform-wide changes and an operator UI (operator console); final prices (N-01).
- **Decisions:** D39; ADR-0023 accepted.
- **Mistakes / lessons:**
  - Quartz cron triggers with `StartNow` do not fire at start-up, so every job also gets a one-shot start-up trigger.
  - Testcontainers fixtures time out when the local stack and dev servers run at the same time: stop them before full runs.
  - The OpenFeature API is a process singleton, so use one domain per host (parallel test hosts).
  - gitleaks flags hex-like test strings (`generic-api-key`): use readable test values.
  - A mutant that does not compile proves nothing.
- **Next:** P1.11.
- **Commits/PR:** #58 … #64 (dafa961), close-out PR.

### 2026-10-25 — P1.9 authorization, slice 5 and close-out (agent)
- **Goal:** owner: "all green go and merge them ur self … and then go p1.9" (merge green PRs, finish P1.9).
- **Done:** #53, #54 merged after green CI; #55 portal settings (roles, field levels, users and roles), `GET /me/access`, step-up pop-up and retry in `@karry/auth`, `MaskedValue`/`SensitiveValue` in `@karry/ui`; close-out: ADR-0022 accepted, T-10/T-13 verified, tracker and sub-phases as built, D38.
- **Verification:** `dotnet test --solution Karry.slnx -c Release` → 504/504; `pnpm -s lint`, typecheck, tests (portal 20, auth 16, ui 54) and `test:a11y` (79) pass; `check:catalogs` 0 problems; links 0 broken; CI all 10 checks green on #55.
- **Not done / deferred:** site choice in the user-roles screen (P1.11); PDF masking (P1.18).
- **Decisions:** D38; ADR-0022 accepted.
- **Mistakes / lessons:** eslint `react-refresh/only-export-components` forbids hooks and components in one file (split `api.ts` / `ApiProvider.tsx`); Radix Select in jsdom needs `scrollIntoView` and pointer-capture polyfills.
- **Next:** P1.10.
- **Commits/PR:** #53 db2689a, #54 48c6f58, #55 970f6cb, close-out PR.

### 2026-10-24 — P1.9 authorization, slices 1–4b (agent)
- **Goal:** sub-phase P1.9 ("go").
- **Done:**
  - #50 engine (merged).
  - #51 permissions on every endpoint, site scopes, anti-escalation.
  - #52 field masking, masking suite, required job `authorization`.
  - #53 custom roles and field levels, with step-up on privileged changes.
- **Verification:** `dotnet test --solution Karry.slnx -c Release` → 492/492 on #53, 504/504 on slice 5; portal 20/20, ui 54 unit + 79 a11y story checks; format and links clean; 30 mutation checks caught across the slices.
  - #54 role assignments, MFA sync, invitation resend (502 tests, 9 mutation checks caught).
  - Slice 5: portal settings screens, `GET /me/access`, step-up pop-up and retry, `MaskedValue`.
- **Not done:** close-out remains (§8). #51/#52 were blocked by Actions billing until the owner made the repository public, then merged.
- **Decisions:** ADR-0022 (proposed) extended with custom roles, step-up decided by handlers, and the per-tenant access-cache generation.
- **Mistakes / lessons:**
  - The first sensitive-input filter enumerated a DbSet. It was found by a test and replaced by static type analysis.
  - Body validation answered 400 before 404 for another tenant's role code. Found by the isolation suite; the addressed resource is now checked first.
- **Next:** P1.9 close-out.
- **Commits/PR:** #50–#54, slice 5 PR.

### 2026-10-24 — P1.8 identity and authentication (agent)
- **Goal:** sub-phase P1.8 ("go").
- **Done:**
  - #45: Keycloak realm and FR/EN login theme with tenant branding, as code; Identity domain.
  - #46: Identity module: invitations, field users, deactivation; Keycloak adapter; first-admin invitation; outbox dispatchers in Workers; demo users; realm generator.
  - #47: failed sign-ins and lockouts copied into the ledger.
  - #48: `@karry/auth` in portal and field; browser E2E in CI.
  - This close-out.
- **Verification:**
  - Backend: 391 tests green in CI and in three local runs on `main`; Identity 91 tests against a real Keycloak and PostgreSQL.
  - Watch item: one earlier local run had 20 failures that were not captured (fresh build while the whole compose stack was running; likely container start-up under memory pressure). If it recurs, keep the log and look at fixture start-up.
  - Frontend: 104 unit tests plus 5 Playwright E2E tests on the CI runner.
  - Local-stack smoke test 34/34 on the CI runner (fresh stack); 15 mutation checks caught.
- **Not done / deferred:** see §8 item 1 (P1.9 carry-overs); device unlock and QR enrolment (P1.15); usage meter (P1.10); tenant logo and colour (P1.11).
- **Decisions:** D37; ADR-0012 update (one realm with Organizations, one account per e-mail).
- **Mistakes / lessons:**
  - Stale-binary mutants, a dedup test without a replay, a paging loop (§4.2).
  - Found two latent bugs: RLS `SET LOCAL` skipped on the second transaction of a unit of work (Npgsql reuses transaction objects); a fixed date in an S3 test (tracker findings 34–37).
- **Next:** P1.9.
- **Commits/PR:** #45–#48, this close-out PR.

### 2026-10-23 — P1.7 frontend foundations (agent)
- **Goal:** sub-phase P1.7 ("go").
- **Done:** #40 `@karry/i18n`; #41 `@karry/ui` design system + Storybook + axe in CI; #42 portal and field shells, switchers, error dialog, `@karry/observability`; #43 `@karry/api-client` generated from the contract; this close-out (`build:pseudo`).
- **Verification:** frontend tests 85 (+ 73 axe cases in Chromium) local and in CI; catalog check 0 problems; built shells checked in Chromium (3 themes × 2 languages, sizes measured, screenshots reviewed).
- **Not done / deferred:** PWA install/offline (P1.15); permission-driven navigation (P1.9/P1.10); company default language (P1.11); error-tracking provider (P1.17).
- **Decisions:** D36.
- **Mistakes / lessons:** Sentry 11 collects personal data by default (caught by the type check); self-kill by pattern again; IPv6-only dev server; backgrounded `&&` chain (§4.2, tracker finding 30).
- **Next:** P1.8.
- **Commits/PR:** #40–#43, this close-out PR.

### 2026-10-23 — P1.6 audit ledger core (agent)
- **Goal:** sub-phase P1.6 ("go").
- **Done:** #34 RFC 8785 canonicalizer (vectors generated by Node.js); #35 Trust module and ledger (same transaction, head lock, triggers + revoked privileges, RLS; provisioning opens each chain); #36 verifier CLI `karry-ledger` + `POST /api/v1/audit/verify`; #37 hourly signed WORM anchoring (Quartz in workers, `anchor-now`, oldest-version rule, alert + runbook); #38 nightly Stryker on Trust. All owner-approved.
- **Verification:** 285 backend tests local and CI; smoke test anchors and verifies against SeaweedFS; nightly run 40006318836 green (mutation 93.75 % / 84.91 %).
- **Not done / deferred:** integrity-check screen and audit viewer (P1.16); KMS key and cloud bucket (P1.17).
- **Decisions:** D35; ADR-0008 implementation notes.
- **Mistakes / lessons:** emptied file through a Python escape; tests that proved nothing until the concurrent actor was frozen at the right step; Stryker found real gaps; another session's uncommitted edit in the shared tree (§4.2, tracker findings 26–29).
- **Next:** P1.7.
- **Commits/PR:** #34–#38, this close-out PR.

### 2026-10-23 — Global competitor analysis and strategic review (agent)
- **Goal:** Answer whether Karry is original, map global/Guinea competitors, identify the deployment gate, and produce a standalone competitor report on the owner's Desktop.
- **Done:** Public-web scan of 80+ vendors across enterprise mining, quarry/aggregate ERP, weighbridge OEM software, fleet/fuel, offline-first field ops, Africa/francophone ERP, horizontal ERP, and chain of custody. Created `/home/dhia/Desktop/Karry-Competitor-Analysis.md` (924 lines, ~50 KB) with 30 deep profiles, feature matrix, gap/reverse-gap analysis, Guinea landscape, pricing benchmarks, threat ranking, and 10-year expansion plan.
- **Findings:** Not original globally; possible Guinea-first white space but no demand proof. Closest threats: QuarryOps ($29/mo offline tamper-evident records), QuarryLink (quote-to-delivery + POD), Extrakto (€199/quarry/mo all-in-one + offline), ArgusBTP (Guinea offline trusted photos), AFTECH (fuel hardware + 7 African offices). Karry's only defensible angle is hash-chained ledger + multi-party signed custody + offline + Guinea localization; most of it is still designed/backlog. Recommended deployment gate: Phase 3 / M2, not P6/M3.
- **Verification:** File written and inspected (`wc -l`, `ls -lh`); URLs spot-checked; no code changes.
- **Not done / deferred:** Docs corrections (42 wrong P1 links, stale `docs/api/` and `kpi-definitions.md` references, status inflation); P0 close-out; P1.6 remains in progress (uncommitted Trust/Ledger work).
- **Decisions:** None recorded as ADR. Strategic recommendation: global architecture, sequential markets; Guinea/UEMOA beachhead; full 225-story catalogue reclassified into core/country-pack/deferred.
- **Next:** Finish P1.6; run P0 close-out interviews; decide primary buyer (contractor vs quarry owner) and pilot weighbridge; deploy at P3/M2.
- **Commits/PR:** None (Desktop deliverable only).

### 2026-10-23 — P1.5 tenancy and isolation (agent)
- **Goal:** sub-phase P1.5 ("go").
- **Done:** #28 roles (`roles.sql`), EF migrations + `Karry.Host migrate`; #29 RLS framework (interceptor, `EnableTenantRls`, EF filter, save check, JWT tenant resolution, secure-by-default modules, tenant-aware outbox/idempotency); #30 Tenancy module (catalog, idempotent provisioning, address check, branding, `seed-demo`), `tests/Karry.Testing`; #31 migration linter + declarations file, reflection type rules; #32 isolation suite over the committed OpenAPI contract, required `tenant-isolation` and `openapi-diff` jobs. All owner-approved.
- **Verification:** `dotnet test --solution Karry.slnx -c Release` → 202 passed (local with Docker and CI run 39986532262); smoke test green (migrate, seed-demo twice); 23 mutation checks caught.
- **Not done / deferred:** tenant defaults from provisioning (P1.8–P1.11 subscribers), 10k mixed-tenant load TC-P1-TEN-005 (P1.18), rate limit on the branding endpoint (P1.18).
- **Decisions:** D34. ADR-0004 implementation notes (SET LOCAL strategy).
- **Mistakes / lessons:** two security mutations first survived (pool reset, non-interleaving race); flaky observability test fixed (process-wide listeners); EF rows-affected offset from prefixed SET LOCAL (tracker findings 19–25).
- **Next:** P1.6 audit ledger core.
- **Commits/PR:** #28–#32, this close-out PR.

### 2026-10-23 — ADR-0021 accepted, P1.4 observability baseline (agent)
- **Goal:** owner accepted ADR-0021 ("keep it with the free tools"), then sub-phase P1.4.
- **Done:** #23 ADR-0021 accepted. #24 OpenTelemetry in Host + Workers (traces incl. Npgsql and outbox dispatch, RED metrics with `module`, Npgsql pool, outbox lag gauge, logs), span attribute allow-list, correlation headers, problem `traceId` fix. #25 Grafana LGTM in compose, 2 dashboards as code, 5 alert rules (OPS-02 AC1) with promtool tests, 5 runbooks, smoke test proves a request end to end in Grafana. Owner approved #24/#25.
- **Verification:** `dotnet test --solution Karry.slnx -c Release` → 106 passed (local with Docker and CI); promtool 9 cases; smoke test 21 checks locally and in CI; 13 mutation checks caught; Semgrep/Trivy 0 findings.
- **Not done / deferred:** UI correlation dialog + Sentry → P1.7; alert routing, DB-CPU and certificate exporters, hosted OTLP → P1.17 (added to the card).
- **Decisions:** D33 (allow-listed telemetry; observability as code). OpenTelemetry 1.20.0 (1.20.1 < 7 days old).
- **Mistakes / lessons:** pkill self-kill again; stacked PR auto-closed when its base branch was deleted (§4.2). CI-only Loki failure = full runner disk (tracker finding 16).
- **Next:** P1.5 tenancy & isolation (owner says "go").
- **Commits/PR:** #23, #24, #25, this close-out PR.

### 2026-10-22 — P1.3 shared kernel and building blocks (agent)
- **Goal:** Sub-phase P1.3.
- **Done:** #17 shared kernel (UUIDv7 IDs, Money/Currency, Result/Error, DomainEvent with stable [EventType] names, AggregateRoot); #18 API conventions (RFC 9457 problems, validation filter, generic 500, OpenAPI outside prod); #19 ModuleDbContext unit of work + transactional outbox + dispatcher (SKIP LOCKED, per-aggregate order, back-off, dead-letter, processed_messages); #20 Idempotency-Key middleware + end-to-end test. Owner approved #18–#20.
- **Verification:** 93 backend tests locally with Docker and in CI; outbox suite stable 3×; nine mutation checks caught.
- **Not done / deferred:** EF migrations + runner (P1.5); excluding generated OpenAPI code from the Host coverage report.
- **Decisions:** TimeProvider instead of IClock; built-in Guid.CreateVersion7; subscribers named IDomainEventSubscriber (CA1711).
- **Next:** P1.4 observability.
- **Commits/PR:** #17–#21.

### 2026-10-22 — P1.2 CI quality gates and governance (agent)
- **Goal:** Sub-phase P1.2 (US-P1-PLT-03).
- **Done:** PRs #6–#10, #15: gates (format, coverage with merged-report gate, architecture, security: gitleaks/Trivy/Semgrep CE), nightly audit + stability, PR/issue templates, CODEOWNERS, SECURITY.md, Renovate config, merge guard. Four negative demo PRs (#11–#14) each failed at the intended gate and were closed.
- **Verification:** `main` run green (all 6 jobs); p50 169 s over 20 runs; 23 tool unit tests; demo evidence in the tracker.
- **Mistakes:** #7 merged red (fixed in #8; guard added); #6–#8 merged without owner approval (approved retroactively).
- **Not done / deferred:** branch protection (owner must buy GitHub Pro), Renovate app install (owner), ADR-0021 acceptance (owner), openapi-diff + tenant-isolation jobs (P1.5), mutation testing (P1.6/P1.12).
- **Decisions:** D31.
- **Next:** P1.3 shared kernel & building blocks.
- **Commits/PR:** #6–#15.

### 2026-10-22 — P1.1 monorepo, template, frontend, local stack (agent)
- **Goal:** Sub-phase P1.1 (US-P1-PLT-01, US-P1-PLT-02).
- **Done:** Owner accepted ADRs 0001–0020 and the merge rule (D29, D30). PRs #1–#4 merged: backend skeleton (Host health, Workers, SharedKernel, BuildingBlocks `IModule`), module template + `tools/new-module.sh` + architecture rules, frontend workspace (portal/field shells, packages, type-aware lint), local stack (compose + smoke test + README). `ci.yml` created (backend, frontend, local-stack).
- **Verification:** local `dotnet test --solution Karry.slnx` 29/29; template test passed; frontend lint/typecheck/test/build passed; CI on `main` run 39841100580 green; smoke test 13/13 in CI (Docker not available locally).
- **Not done / deferred:** PLT-02 AC2 seed data (P1.5/P1.8), AC3 onboarding timing (P1.18), ArchUnitNET type-level rules (P1.5). Docker for the agent still pending.
- **Decisions:** D29, D30.
- **Next:** P1.2 (CI quality gates, branch protection, Renovate, templates).
- **Commits/PR:** #1, #2, #3, #4.

### 2026-10-22 — Push access, green CI, Phase 1 sub-phase plan, P1.0 (agent)
- **Goal:** Get push access, make CI green, plan Phase 1 in sub-phases, start P1.0.
- **Done:** Owner set up `gh` (push works). Force-pushed the trailer-free history (owner approved; remote trees verified identical first). Fixed CI: xunit v3 `--report-xunit-trx`, stale pnpm lockfile, pg-extension readiness race; 4 consecutive green runs. `spikes-explained.md` (plain English). Phase 1 `sub-phases.md` (P1.0 – P1.18) + `tracker.md` + 26 new TC-P1 IDs. P1.0: versions verified (tech stack §7.1.1), ADR-0017 … 0020, MinIO replaced by SeaweedFS in docs, Node 24 + actionlint installed.
- **Verification:** GitHub Actions runs 39828081288, 39828265888, 39828509297, 39828751049 all green; link checker 0 broken; actionlint clean.
- **Not done / deferred:** Docker access (owner), merge rule (owner), ADR acceptance (owner). Local branch `backup/pre-trailer-removal` still exists (never pushed).
- **Decisions:** D27, D28.
- **Next:** Owner inputs for P1.0, then P1.1.
- **Commits/PR:** see `git log`.

### 2026-10-21 — Phase 0 engineering work packages (agent)
- **Goal:** Remove AI co-author attribution; start implementing Phase 0.
- **Done:** History rewritten without `Co-Authored-By` trailers (backup branch `backup/pre-trailer-removal`), D25 + guidelines §20.2. Phase 0: spikes WP-P0-10/11/12/13 in `spikes/` with tests + CI (`spikes.yml`, `docs.yml`); 16 ADRs (Proposed); formula handbook with 22 golden examples (draft); threat model v1; discovery kit (interviews, site visit, pricing, pilot charter, legal brief); Phase 0 tracker; RLS guidance corrected across docs.
- **Verification:** `dotnet test --solution spikes/Karry.Spikes.slnx` → 15/15 passed (PostgreSQL 17.5 local); `pnpm test` (Playwright) → 10/10 passed; mutation checks caught all injected bugs; RLS benchmark run 3×; `actionlint` clean; link checker 0 broken. Extension check NOT run locally (no Docker); runs in CI.
- **Not done / deferred:** Human WPs (01, 02, 05–08, 14, 15, 19, 20); device-lab tablet run; managed-provider extension check; event storming; prototype.
- **Decisions:** D26; ADRs 0001–0016 proposed.
- **Next:** Owner force-push + Phase 0 human work + ADR acceptance (see §8).
- **Commits/PR:** see `git log`.

### 2026-10-21 — Docs reorganized into a scalable structure (Claude Opus 5.5)
- **Goal:** Organize `docs/` so it scales and agents can search it easily.
- **Done:** Split `MASTER_PLAN.md` + `PHASES_DETAILED_PLAN.md` by script into `docs/01-product … 07-management`, `adr/`, `08-user-help/`, one folder per phase (`04-phases/phase-NN-*/plan.md`); moved LLM docs to `docs/00-llm/`; added YAML front matter + breadcrumbs to every doc, folder READMEs, `docs/README.md` (folder map, "where do I find…" table, conventions, legacy §-reference map); rewrote all `§N` cross-references into relative links with anchors; added `tools/docs/check_links.py`; updated `CLAUDE.md`, `AGENTS.md`, guidelines §19 (docs organization rules).
- **Verification:** `python3 tools/docs/check_links.py .` → `0 broken link(s)`; probe file with a broken link was detected (then removed).
- **Not done / deferred:** No CI job runs the link checker yet. Add it to `docs.yml` when CI is created in P1 (`US-P1-PLT-03`).
- **Decisions:** D24.
- **Next:** Push to GitHub (owner credentials needed), then ADRs 0001–0016.
- **Commits/PR:** see `git log`.

### 2026-10-21 — LLM guidelines, hand-off cache, push to GitHub (Claude Opus 5.5)
- **Goal:** Create `llm_hands_off.md` (this file) and `llm_guidelines.md`; push all docs to the repository.
- **Done:** Created both files; added root `CLAUDE.md`, `AGENTS.md` and `.gitignore`; first commit created on `main`.
- **Verification:** Docs only; checked Markdown structure and Mermaid label quoting. `git push` FAILED: no GitHub credentials on the machine (no `gh`, no SSH key, HTTPS askpass crashed). Owner must push.
- **Not done / deferred:** No CI yet (planned in P1, `US-P1-PLT-03`); ADRs not yet written.
- **Decisions:** None new; D1–D23 consolidated here from earlier sessions.
- **Mistakes / lessons:** See §4.2.
- **Next:** Owner answers open questions §7; then ADRs 0001–0016.
- **Commits/PR:** `fa52d12` on `main` (push pending owner credentials). Note: file paths in entries below this one refer to the pre-reorganization layout; see the legacy map in `docs/README.md`.

### 2026-10-21 — Detailed phase plan (Claude Opus 5.5)
- **Goal:** Detailed plan of every phase with user stories, AC, test plans; subscription business model; microservices opinion.
- **Done:** `docs/PHASES_DETAILED_PLAN.md` (~3,500 lines, 225 stories, test case catalogue per phase, appendices); link added in [Roadmap Summary §21](../04-phases/roadmap-summary.md).
- **Decisions:** D1, D2, D16, D17, D18.
- **Next:** Guidelines and hand-off files (done in next session).

### 2026-10-20 — Master architecture plan (Claude Opus 5.5)
- **Goal:** Turn the LaTeX codex into a professional architecture & roadmap document.
- **Done:** `docs/MASTER_PLAN.md` (29 sections): spec review with corrections, stack, C4 diagrams, bounded contexts, corrected math, data, offline sync, trust layer, security, AI, analytics, DevOps, testing, roadmap, risks, ADR index.
- **Decisions:** D3–D15, D19–D23.
- **Mistakes / lessons:** Mermaid parentheses issue (fixed with quoting).

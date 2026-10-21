# LLM Hand-Off Log (Project Memory Cache)

> **Purpose.** This file holds the working memory of the Karry project for any LLM or AI coding agent that picks up the work. It records **what has been decided, what has been done, what went wrong, and what to do next**, so the next agent does not repeat mistakes, re-open settled decisions, or redo finished work.
>
> **Every LLM session MUST read this file first and MUST update it before ending** (see §9 for the protocol). The behaviour rules themselves are in [`llm_guidelines.md`](./llm_guidelines.md).

| Field | Value |
|---|---|
| Project | Karry Platform: Enterprise Quarry & Mining Management SaaS |
| Repository | `github.com/diya-thabet/Karry-Platform` |
| Default branch | `main` |
| Current phase | **Pre-Phase 0**: documentation baseline done, no application code yet |
| Last updated | 2026-10-21 |
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
3. [`MASTER_PLAN.md`](./MASTER_PLAN.md): architecture, stack, corrected formulas, security, data model.
4. [`PHASES_DETAILED_PLAN.md`](./PHASES_DETAILED_PLAN.md): phases, user stories (`US-…`), acceptance criteria, test cases (`TC-…`), exit gates, business model.
5. `docs/adr/` (when it exists): accepted architecture decisions.
6. `git log --oneline -30` and open PRs/issues, to see what changed since this file was last updated.
7. If this file and the code disagree, **the code and git history are the truth**. Fix this file.

---

## 2. Project Snapshot

- **What:** Multi-tenant SaaS (monthly/annual subscription), offline-first PWA, for quarries, crushing plants, sand washing and open-pit mines.
- **Core value:** trusted field data (anti-fraud on fuel, meters, scale tickets), predictive maintenance, multi-warehouse logistics, plant mass balance, per-machine finance (DSCR), weather-aware planning, AI copilot.
- **Business model:** per-site-per-month plans (Starter / Professional / Business / Enterprise) + add-ons; field users unlimited; see `PHASES_DETAILED_PLAN.md` §2.
- **Languages:** French + English from day 1 (Arabic RTL-ready later).
- **Owner / product owner:** the repository owner (GitHub `diya-thabet`). The human makes product decisions; the LLM acts as senior architect/engineer/QA/DevOps.

---

## 3. Settled Decisions (do not re-open without an ADR)

| # | Decision | Where documented | Why (one line) |
|---|---|---|---|
| D1 | **Modular monolith**, not microservices, at start; ~4 custom deployables (API, Worker, Python analytics sidecar, Edge agent) + Keycloak | Phases §3, Master §7.2 | Small team, time-to-revenue, transactional trust guarantees, offline sync coherence, margin |
| D2 | Extraction to services only on measured triggers (telemetry ingestion and AI gateway are the first candidates) | Phases §3.6 | Avoid premature distribution |
| D3 | Backend **.NET 10 LTS**, ASP.NET Core, EF Core 10 | Master §7 | .NET 9 support ends Nov 2026; .NET 10 is supported to Nov 2028 |
| D4 | **PostgreSQL 17 or 18** (final pick in Phase 0 spike) + PostGIS + TimescaleDB + pgvector | Master §7, WP-P0-13 | Extension availability on managed provider must be verified |
| D5 | Frontend React 19 + TypeScript strict + Vite + Tailwind v4 + shadcn/ui + TanStack Query/Router; Dexie (IndexedDB) + Workbox | Master §7 | Offline-first PWA on low-end Android |
| D6 | Charts: **Apache ECharts** primary, Recharts only for small widgets | Master §7, ADR-0014 | Recharts (SVG) slows beyond ~5k points |
| D7 | **Core business math lives in .NET**, Python only for ML/forecasting/anomaly models | Master §3.2 T3 | Single source of truth per rule |
| D8 | Multi-tenancy: pool model with PostgreSQL **RLS** + `SET LOCAL app.tenant_id` per transaction + EF global filters; silo option for Enterprise | Master §14.4 | Defense in depth, low cost per tenant |
| D9 | Identity: **Keycloak** (OIDC + PKCE, MFA for privileged roles); in-app RBAC + ABAC (site/warehouse scope) + field-level masking applied in API serialization | Master §14 | Standards, enterprise SSO later |
| D10 | Signatures = handwritten strokes (evidence) **+** device ECDSA P-256 (WebCrypto, non-extractable) **+** server KMS countersignature, optional RFC 3161 timestamp | Master §13.2 | Real cryptography, not "canvas + hash" |
| D11 | Audit = append-only, hash-chained ledger per tenant, written in the same transaction as the business change, anchored hourly to WORM object storage | Master §13.3 | Tamper-evident |
| D12 | **Immutable facts**: signed records are never edited; corrections = reversal + new version linked to original | Master §6, §13 | Trust is the product |
| D13 | Every volume carries a **material state** (BANK / LOOSE / COMPACTED); weighbridge mass is authoritative for invoicing | Master §3.1 M2, §10.1 | Swell factor errors misprice loads by 30–60 % |
| D14 | Solvency = **DSCR** (net cash margin / debt service), not the codex formula | Master §3.1 M7 | Codex formula was dimensionally wrong |
| D15 | AI never computes numbers: **tool-calling over a governed semantic layer**, no free-form text-to-SQL; provider-agnostic gateway; Claude default (`claude-sonnet-5-5` Q&A, `claude-haiku-5-5` routing, `claude-opus-5-5` deep analysis); sovereign self-hosted option | Master §15, Phases §15 | Prevent hallucinated numbers & data leaks |
| D16 | **Plans/entitlements/usage meters built in Phase 1**, not at GA | Phases §2.4, EP-P1-05 | Needed to sell from Phase 3 |
| D17 | **Field capture is never blocked by billing state** (Restricted state keeps field capture working) | Phases §2.5 | Losing shift data destroys trust |
| D18 | Field users are **unlimited** on all plans; we charge per site + assets + office seats | Phases §2.3 | Per-operator pricing causes shared logins, which kills the anti-fraud value |
| D19 | Money = `decimal`/`numeric(19,4)` + ISO-4217 currency; never floating point | Master §3.1 M11 | Financial correctness |
| D20 | IDs = UUIDv7 (client-generatable offline) | Master §11.2 | Offline creation + index locality |
| D21 | Trunk-based development, feature flags, server supports N-2 PWA client versions | Phases §4.7 | Offline devices update late |
| D22 | Avoid libraries that switched to commercial licences (MediatR, AutoMapper, MassTransit v9) unless budgeted; check licences of QuestPDF and others | Master §7.1, Risk R12 | Licence cost/legal risk |
| D23 | Timeline: 8 weeks is NOT realistic; plan is P0–P10 over ~13 months; Starter sellable after P3 | Phases §6 | Codex timeline rejected |

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
| RLS example `tenant_id = current_setting(...)` | uuid vs text, unsafe with pooling | `::uuid` cast + `SET LOCAL` + `FORCE RLS` + non-owner role |
| Python FastAPI as core math engine | Duplicated rules across runtimes | D7 |
| 8-week roadmap | Unrealistic | D23 |

### 4.2 Mistakes made by LLM sessions (learn from them)

| Date | Mistake | Fix / rule going forward |
|---|---|---|
| 2026-10-20 | Mermaid flowchart node labels containing parentheses `T1[get_metric(a, b)]` break GitHub rendering | **Always quote Mermaid labels that contain `()`, `:`, `/`, `#`, `<br/>` or punctuation: `T1["get_metric(a, b)"]`.** Check every diagram before committing |
| 2026-10-20 | First master plan put subscription billing/entitlements only in the final GA phase | Monetization infrastructure (plans, entitlements, usage) belongs in Phase 1. Always ask "how does this make money and when?" when sequencing |
| 2026-10-21 | Story index was hand-written risk → generated by script from headings instead | When a document has a derived index/table, **generate it from the source** rather than hand-typing it |

> Add every new mistake here, including small ones. The next agent benefits most from this section.

---

## 5. Traps, Gotchas & Environment Notes

- **Owner's environment:** Fedora Linux (kernel 7.x), bash, repo at `~/Desktop/Karry-Platform`. Git user `diya-thabet`.
- **Language of the owner:** writes in English (informal); product is bilingual FR/EN. Domain words are often French (*carrière, pontier, magasin général, tapis roulant / sebta, panne compteur, courbes et graphiques*). Keep the glossary in `MASTER_PLAN.md` §27 up to date.
- **Owner preference:** wants very detailed, "very pro", enterprise-grade outputs and firm architect opinions with reasoning, not neutral option lists.
- **Git push from the agent fails**: HTTPS remote, no stored credentials, `gh` not installed, no SSH key (as of 2026-10-21). Ask the owner to push or to set up `gh auth login` / an SSH key.
- **No application code exists yet.** No CI workflow exists yet. Do not claim tests or pipelines exist until they do.
- **GitHub Actions:** CI must be created in Phase 1 (`US-P1-PLT-03`). Until then, only docs exist. When creating it, pin third-party actions by commit SHA (see guidelines §9).
- **Versions:** always pin exact versions at the time of implementation and verify they are current and supported. Do not trust remembered version numbers without checking (release notes, `dotnet --list-sdks`, `npm view <pkg> version`).
- **Mermaid on GitHub:** see §4.2. Gantt charts render, but keep task IDs simple (`p0`, `p1`).
- **Large docs:** when writing very long Markdown, write in sections/appends and re-verify line count and anchors afterwards.
- **Offline PWA facts:** iOS Safari lacks Background Sync and may evict storage. Always call `navigator.storage.persist()`. Test on real low-end Android.
- **WebCrypto non-extractable keys** survive restarts in IndexedDB but are lost on "clear site data". The re-enrolment flow must exist.
- **EF Core + RLS:** set `app.tenant_id` with `SET LOCAL` inside the transaction, never at session level (connection pooling would leak it).

---

## 6. Current State of the Repository

```
Karry-Platform/
├── CLAUDE.md                     ← pointer for Claude Code (auto-loaded), points to docs below
├── AGENTS.md                     ← pointer for other AI coding agents
├── .gitignore                    ← baseline ignores (.NET, Node, Python, IaC, secrets)
└── docs/
    ├── MASTER_PLAN.md            ← architecture & stack (v1.0)
    ├── PHASES_DETAILED_PLAN.md   ← phases, 225 user stories, AC, test plans, business model (v1.0)
    ├── llm_guidelines.md         ← mandatory working rules for LLM agents
    └── llm_hands_off.md          ← this file
```

| Phase | Status |
|---|---|
| P0 Discovery | Not started (docs baseline ready to support it) |
| P1 Foundation | Not started |
| P2–P11 | Not started |

---

## 7. Open Questions Awaiting the Product Owner

Copied from `MASTER_PLAN.md` §28. Remove an item when it is answered and record the answer in §3 or the relevant doc.

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

1. **Owner answers §7** (at least 1, 2, 3, 13, 14).
2. Write ADRs `docs/adr/0001` … `0016` (list in `MASTER_PLAN.md` §24) in MADR format.
3. Write `docs/domain/formulas.md` with worked numeric examples for every formula (WP-P0-04).
4. Write `docs/security/threat-model.md` v1 (WP-P0-18).
5. Phase 0 spikes (WP-P0-10 … WP-P0-13) in `spikes/` (throwaway code, report each result).
6. Phase 1 start: scaffold the monorepo per `MASTER_PLAN.md` §26 and create the CI pipeline (`US-P1-PLT-01..05`) **before** any feature code.

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

### 2026-10-21 — LLM guidelines, hand-off cache, push to GitHub (Claude Opus 5.5)
- **Goal:** Create `llm_hands_off.md` (this file) and `llm_guidelines.md`; push all docs to the repository.
- **Done:** Created both files; added root `CLAUDE.md`, `AGENTS.md` and `.gitignore`; first commit created on `main`.
- **Verification:** Docs only; checked Markdown structure and Mermaid label quoting. `git push` FAILED: no GitHub credentials on the machine (no `gh`, no SSH key, HTTPS askpass crashed). Owner must push.
- **Not done / deferred:** No CI yet (planned in P1, `US-P1-PLT-03`); ADRs not yet written.
- **Decisions:** None new; D1–D23 consolidated here from earlier sessions.
- **Mistakes / lessons:** See §4.2.
- **Next:** Owner answers open questions §7; then ADRs 0001–0016.
- **Commits/PR:** initial commit on `main` (push pending owner credentials).

### 2026-10-21 — Detailed phase plan (Claude Opus 5.5)
- **Goal:** Detailed plan of every phase with user stories, AC, test plans; subscription business model; microservices opinion.
- **Done:** `docs/PHASES_DETAILED_PLAN.md` (~3,500 lines, 225 stories, test case catalogue per phase, appendices); link added in `MASTER_PLAN.md` §21.
- **Decisions:** D1, D2, D16, D17, D18.
- **Next:** Guidelines and hand-off files (done in next session).

### 2026-10-20 — Master architecture plan (Claude Opus 5.5)
- **Goal:** Turn the LaTeX codex into a professional architecture & roadmap document.
- **Done:** `docs/MASTER_PLAN.md` (29 sections): spec review with corrections, stack, C4 diagrams, bounded contexts, corrected math, data, offline sync, trust layer, security, AI, analytics, DevOps, testing, roadmap, risks, ADR index.
- **Decisions:** D3–D15, D19–D23.
- **Mistakes / lessons:** Mermaid parentheses issue (fixed with quoting).

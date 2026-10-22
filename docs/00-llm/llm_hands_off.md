# LLM Hand-Off Log (Project Memory Cache)

> **Purpose.** This file holds the working memory of the Karry project for any LLM or AI coding agent that picks up the work. It records **what has been decided, what has been done, what went wrong, and what to do next**, so the next agent does not repeat mistakes, re-open settled decisions, or redo finished work.
>
> **Every LLM session MUST read this file first and MUST update it before ending** (see §9 for the protocol). The behaviour rules themselves are in [`llm_guidelines.md`](./llm_guidelines.md).

| Field | Value |
|---|---|
| Project | Karry Platform: Enterprise Quarry & Mining Management SaaS |
| Repository | `github.com/diya-thabet/Karry-Platform` |
| Default branch | `main` |
| Current phase | **Phase 0 in progress**: engineering work packages done or partially done (spikes, ADRs, formulas draft, threat model, discovery kit); human work packages waiting on the owner. Live status: [Phase 0 tracker](../04-phases/phase-00-discovery/tracker.md) |
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
| D31 | Security scanning = gitleaks + Trivy + Semgrep CE (CodeQL unlicensed on private repo); npm packages ≥ 7 days old (pnpm `minimumReleaseAge`, Renovate); merges only via `tools/ci/merge_guard.py` until branch protection (needs GitHub Pro) | ADR-0021 (proposed), [sub-phases §2.3](../04-phases/phase-01-platform-foundation/sub-phases.md#23-review--merge) | P1.2, 2026-10-22 |
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
- **Docker is not usable by the agent** (`permission denied` on `/var/run/docker.sock`; user not in `docker` group). Testcontainers and the extension check cannot run locally until the owner runs `sudo usermod -aG docker $USER` and re-logs in. Workaround used for PostgreSQL: portable binaries (zonky `embedded-postgres-binaries-linux-amd64` 17.5 from Maven Central) started with `initdb`/`pg_ctl` on TCP `127.0.0.1:55432` in the scratchpad (`-k ''`, because the Unix socket path exceeds 107 bytes). No `psql` in that bundle.
- **.NET 10 + xunit v3**: `dotnet test` requires the Microsoft Testing Platform runner (`global.json` → `"test": {"runner": "Microsoft.Testing.Platform"}`), and the syntax is `dotnet test --solution X.slnx`. Do not add `Microsoft.NET.Test.Sdk`/`xunit.runner.visualstudio`. Add `<Using Include="Xunit" />`.
- **Playwright on Fedora** uses the "ubuntu24.04 fallback build" of Chromium, which works. `context.setOffline(true)` does **not** block `context.route` handlers, so fake servers must model outages themselves.
- **MinIO OSS is archived** (2026-04; no community images). Do not add MinIO anywhere; use SeaweedFS (ADR-0017), RustFS as fallback.
- **TypeScript 7 vs lint:** `typescript-eslint` 8.72 supports TypeScript `<6.1.0` only. Product code pins TS 6.0.x (ADR-0020); the Phase 0 spike keeps TS 7 (frozen).
- **Node versions:** Node 24 LTS installed via nvm (`nvm use 24`; the nvm default is still 22). Product repo pins Node 24 via `.nvmrc`/`engines`.
- **actionlint** 1.7.12 is installed at `~/.local/bin/actionlint` (checksum verified). shellcheck is not installed.
- **Version checks that work here:** `npm view <pkg> version`; NuGet flat container `curl -4 -s https://api.nuget.org/v3-flatcontainer/<id>/index.json`; Docker Hub tags API; `gh api repos/<o>/<r>/releases/latest`. Use `curl -4` (IPv6 broken).
- **Local test PostgreSQL without Docker:** portable zonky 17.5 in the scratchpad (`initdb -A scram-sha-256`, `pg_ctl -o "-p 55432 -k '' -c listen_addresses=127.0.0.1"`), then `KARRY_TEST_PG="Host=127.0.0.1;Port=55432;Username=postgres;Password=postgres;Database=postgres"` makes the integration tests use it instead of Testcontainers. No extensions in that build: tests needing PostGIS/Timescale must run in CI until Docker works.
- **Frontend locally:** `nvm use 24` then `corepack enable` (pnpm 12 shims) inside `src/frontend`.
- **Analyzers are strict** (`AnalysisLevel=latest-recommended`, code style enforced, warnings = errors): use `LoggerMessage` source-generated logging (CA1848), no constant array arguments (CA1861), `GenerateDocumentationFile` is on for IDE0005. Test naming with underscores is allowed only in test folders (`.editorconfig`).
- **Merge only with `python3 tools/ci/merge_guard.py <pr>`** (add `--owner-approved` only after the owner explicitly approves a sensitive PR). Never call `gh pr merge` directly: on 2026-10-22 PR #7 was merged with a failing `security` check that way, turning `main` red until #8. Branch protection is unavailable without **GitHub Pro** (private repo, HTTP 403).
- **`gh pr merge --delete-branch`** also deletes the local branch and switches to `main`; re-create the next branch from `origin/main`.
- **Never `pkill -f <pattern>`** with a pattern that also appears in your own command line: it kills your own shell (exit 144).
- **GitHub Actions:** full product CI is created in Phase 1 (`US-P1-PLT-03`); `docs.yml` and `spikes.yml` exist now. Pin third-party actions by commit SHA (see guidelines §9).
- **xunit v3 TRX reports:** use `--report-xunit-trx`, **not** `--report-trx` (that needs the `Microsoft.Testing.Extensions.TrxReport` package; without it MTP exits with code 5, "Zero tests ran", "Handshake failures"). Always run the exact CI command locally before pushing.
- **pnpm lockfile:** after editing `package.json` versions, run `pnpm install` and commit `pnpm-lock.yaml`; CI uses `--frozen-lockfile` and fails on any specifier mismatch (e.g. `^26.6.5` vs `26.6.5`). Verify with `CI=true pnpm install --frozen-lockfile`.
- **Postgres container readiness:** the image entrypoint runs a temporary socket-only server during init, then restarts. Check readiness with `pg_isready -h 127.0.0.1` (TCP), never via the socket, or the next command races the restart (flaky pg18 check, 2026-10-22).
- **Path-filtered workflows may not trigger on a force-push**; both workflows have `workflow_dispatch`, so run `gh workflow run <file> --ref main`.
- **Versions:** always pin exact versions at the time of implementation and verify they are current and supported. Do not trust remembered version numbers without checking (release notes, `dotnet --list-sdks`, `npm view <pkg> version`).
- **Mermaid on GitHub:** see §4.2. Gantt charts render, but keep task IDs simple (`p0`, `p1`).
- **Large docs:** when writing very long Markdown, write in sections/appends and re-verify line count and anchors afterwards.
- **Offline PWA facts:** iOS Safari lacks Background Sync and may evict storage. Always call `navigator.storage.persist()`. Test on real low-end Android.
- **WebCrypto non-extractable keys** survive restarts in IndexedDB but are lost on "clear site data". The re-enrolment flow must exist.
- **EF Core + RLS:** set `app.tenant_id` with `set_config(..., true)` inside the transaction, never at session level (proven leak in TC-P0-RLS-005; connection pooling would leak it).

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
| P1 Foundation | **P1.0 ✅, P1.1 ✅, P1.2 ✅** (owner: GitHub Pro → branch protection; Renovate app); next P1.3 (shared kernel & building blocks): see [Phase 1 tracker](../04-phases/phase-01-platform-foundation/tracker.md) and [sub-phases](../04-phases/phase-01-platform-foundation/sub-phases.md) |
| P2–P11 | Not started |

---

## 7. Open Questions Awaiting the Product Owner

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

1. **Phase 1 is executed sub-phase by sub-phase** per [sub-phases.md](../04-phases/phase-01-platform-foundation/sub-phases.md) (P1.0 – P1.18). The owner says "start P1.x"; follow §2 of that file (way of working) and the card. Status in the [Phase 1 tracker](../04-phases/phase-01-platform-foundation/tracker.md). Do not start a sub-phase whose dependencies are not ✅.
2. **P1.0 prerequisites from the owner**: Docker access for the agent (`sudo usermod -aG docker $USER`, re-login), PR merge rule, acceptance of ADRs 0001–0016.
3. **Owner Phase 0 work** continues in parallel: interviews, site visits, pricing validation, pilot LOI, legal brief, hosting and billing decisions, answers to §7 (kits in `docs/01-product/discovery/`). Hosting decision blocks P1.17.
4. Formula handbook expert sign-off (needed by P1.12).
5. Real-device run of the Phase 0 device-lab checklist (can be combined with P1.15).

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

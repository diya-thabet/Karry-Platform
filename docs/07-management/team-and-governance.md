---
title: "Team, Governance & Ways of Working"
area: management
tags: [team, governance, standards]
source: "MASTER_PLAN.md §22"
status: baseline
last_updated: 2026-10-21
---

# Team, Governance & Ways of Working

> [Docs index](../README.md) › [07-management](README.md)

## 22.1 Recommended core team

| Role | FTE | Notes |
|---|---|---|
| Software Architect / Tech Lead | 1 | Owns this document & ADRs |
| Senior .NET backend engineers | 2 | Domain, trust layer, sync |
| Senior frontend engineer (PWA/offline) | 1–2 | Offline, signatures, charts |
| Full-stack / data engineer (Python + SQL) | 1 | Analytics sidecar, TimescaleDB, reporting (from P4) |
| DevOps / SRE (can be part-time early) | 0.5–1 | IaC, CI/CD, observability |
| QA / test automation | 1 | Device lab, E2E, offline scenarios |
| Product owner with quarry domain knowledge | 1 | **Critical** — owns backlog & acceptance |
| UX/UI designer | 0.5 | Field ergonomics |
| Domain expert (mining engineer), advisory | 0.2 | Formula validation, golden datasets |

## 22.2 Cadence

- 2-week sprints; sprint review on staging with the pilot customer from Phase 2 onwards.
- Phase exit review against exit criteria; re-plan next phase.
- Architecture review weekly (30 min); ADR for any decision affecting more than one module.
- Security champion rotates per sprint; threat model updated each phase.

## 22.3 Engineering standards

- C#: nullable enabled, warnings as errors, analyzers (Roslyn + SonarAnalyzer), `dotnet format`.
- TypeScript: `strict`, ESLint + Prettier, no `any` in domain code.
- Conventional Commits, PR template (what/why/how tested/screenshots), CODEOWNERS (trust/security modules require architect review).
- Documentation as code in `docs/` (Markdown + Mermaid).

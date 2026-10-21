---
title: "Defect Severity & Triage SLAs"
area: quality
tags: [bugs, severity, sla]
source: "PHASES_DETAILED_PLAN.md §27"
status: baseline
last_updated: 2026-10-21
---

# Defect Severity & Triage SLAs

> [Docs index](../README.md) › [05-quality](README.md)

| Severity | Definition | Examples | Response | Fix target |
|---|---|---|---|---|
| **Sev-1 Critical** | Data loss/corruption, security breach, cross-tenant leak, ledger integrity failure, production down | Entries lost after sync; tenant sees another tenant's data | 15 min, 24/7 | Hotfix ≤ 4 h (workaround), root fix ≤ 48 h |
| **Sev-2 Major** | Core workflow blocked for many users, no workaround | Cannot issue tickets; review queue broken | 1 h (business hours, 24/7 Enterprise) | ≤ 1 business day |
| **Sev-3 Moderate** | Feature impaired, workaround exists | Chart wrong filter; PDF layout broken | 1 business day | Next release (≤ 2 weeks) |
| **Sev-4 Minor** | Cosmetic, typo, minor UX | Translation typo | 3 business days | Backlog prioritised |

Triage: daily during pilot/hypercare, then twice weekly. Every Sev-1/Sev-2 gets a regression test and a post-mortem.

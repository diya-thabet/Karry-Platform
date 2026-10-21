---
title: "Release Management & Go-Live Checklists"
area: operations
tags: [releases, checklists, incidents]
source: "PHASES_DETAILED_PLAN.md §21"
status: baseline
last_updated: 2026-10-21
---

# Release Management & Go-Live Checklists

> [Docs index](../README.md) › [06-operations](README.md)

## 21.1 Release types

| Type | Frequency | Approval | Notes |
|---|---|---|---|
| Patch (bug fix) | As needed | Tech lead | Hotfix path; same pipeline; canary |
| Minor (features behind flags) | Weekly (pilot) → continuous | PO + tech lead | Feature flags per tenant |
| Phase release | End of each phase | Phase exit review | Release notes FR/EN, webinar for customers |
| Breaking API change | Rare | Architecture review | New API version, 6-month deprecation |

## 21.2 Production release checklist

- [ ] All CI gates green; no open Sev-1/Sev-2 for included scope.
- [ ] DB migrations reviewed; expand/contract respected; backup verified < 24 h.
- [ ] Feature flags configured (default off for new risky features).
- [ ] PWA version compatibility (N-2) verified in staging with old clients.
- [ ] Load test passed if performance-sensitive change.
- [ ] Release notes FR/EN; help articles updated.
- [ ] Support team briefed; known issues documented.
- [ ] Rollback plan stated (app & DB).
- [ ] Canary 10 % for 1 h with error budget watch → 100 %.
- [ ] Post-release verification (smoke tests in prod, ledger verifier run, sync health).

## 21.3 Incident management

- Severity matrix ([Appendix E – Defect Severity & Triage SLAs](../05-quality/defect-severity-and-triage.md)), on-call rotation, incident commander role, status page updates within 15 min for Sev-1, customer communication templates (FR/EN), post-mortem (blameless) within 5 business days for Sev-1/Sev-2.

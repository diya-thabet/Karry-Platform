---
title: "Open Questions & Next Steps"
area: management
tags: [questions, next-steps]
source: "MASTER_PLAN.md §28; MASTER_PLAN.md §29"
status: baseline
last_updated: 2026-10-21
---

# Open Questions & Next Steps

> [Docs index](../README.md) › [07-management](README.md)

## Open Questions for Domain Experts

1. Which countries/markets are targeted first (drives legal, currency, hosting region, languages)?
2. Typical connectivity at target sites: none / intermittent 3G-4G / Wi-Fi at office only?
3. Which weighbridge indicator brands/models are installed at the pilot site?
4. Do pilot machines have OEM telematics subscriptions?
5. How is revenue attributed per machine today (if at all)? Which policy will the CFO accept?
6. Invoicing basis by customer: tonnes or m³? Who sets densities, and how often are they re-measured?
7. Are shift patterns fixed (2×12 h) or variable? Are there more than two shifts?
8. Who are the approvers today, and how many entries per controller per shift (to size the review UX)?
9. Which wear parts cause the most expensive downtime, and what are real lead times?
10. Is a dedicated/on-prem deployment required by any target customer?
11. Required document retention periods for tickets, waybills, and financial records in target countries?
12. Is integration with an existing ERP/accounting system required for the pilot?

---

## Next Steps

1. **Review this plan** and answer the open questions in [§28](#open-questions-for-domain-experts).
2. Approve the **corrections in [§3](../03-domain/spec-review-corrections.md)** (or provide counter-evidence) — they change the data model.
3. Schedule Phase 0 workshops and the pilot site visit.
4. Write ADRs 0001–0015 and the domain formula handbook (`docs/03-domain/formulas-worked-examples.md`) with worked numeric examples.
5. Scaffold the repository per [§26](../02-architecture/repository-structure.md) and start Phase 1.

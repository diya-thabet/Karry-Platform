---
title: "Product & Business KPIs"
area: product
tags: [kpis, mrr, metrics, dora]
source: "PHASES_DETAILED_PLAN.md §22"
status: baseline
last_updated: 2026-10-21
---

# Product & Business KPIs

> [Docs index](../README.md) › [01-product](README.md)

## 22.1 Business KPIs (tracked monthly by founders)

| KPI | Definition | Target (24 months) |
|---|---|---|
| MRR / ARR | Recurring revenue | See [§2.8](business-model-and-pricing.md#28-illustrative-revenue-path-assumptions-not-a-forecast) path |
| New MRR, Expansion MRR, Churned MRR | Movements | Expansion ≥ 30 % of new MRR |
| Net Revenue Retention | (Start MRR + expansion − contraction − churn) / Start MRR | ≥ 110 % |
| Logo churn | Tenants lost / tenants at start | ≤ 1.5 % / month |
| Trial → paid conversion | | ≥ 20 % (self-serve), ≥ 50 % (sales-assisted) |
| CAC & payback | Sales+marketing cost / new customers | Payback ≤ 12 months |
| Gross margin | (Revenue − COGS) / Revenue | ≥ 80 % |
| Sites per customer | Avg | Growing quarter over quarter |

## 22.2 Product KPIs

| KPI | Definition | Target |
|---|---|---|
| Time-to-first-verified-shift | Sign-up → first verified entry | < 48 h median |
| Field coverage | Verified entries / expected entries | ≥ 95 % |
| Approval latency | Submission → approval median | < 12 h |
| Sync reliability | Commands accepted without manual intervention | ≥ 99.9 % |
| Flag precision | Flags resolved as real issue / all resolved flags | ≥ 40 % (tunes thresholds) |
| Weekly active office users / seats | | ≥ 60 % |
| Copilot weekly usage (Business) | Sessions per executive per week | ≥ 2 |
| NPS | Quarterly survey | ≥ 40 |

## 22.3 Engineering KPIs (DORA + quality)

| KPI | Target |
|---|---|
| Deployment frequency | Daily (staging), weekly → daily (prod) |
| Lead time for changes | < 2 days |
| Change failure rate | < 10 % |
| MTTR | < 1 h for Sev-2, < 4 h for Sev-1 |
| Escaped defects per release | Decreasing trend |
| Ledger verification failures | 0 |

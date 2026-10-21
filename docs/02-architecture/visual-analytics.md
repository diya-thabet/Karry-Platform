---
title: "Visual Analytics Architecture"
area: architecture
tags: [dashboards, charts, echarts]
source: "MASTER_PLAN.md §16"
status: baseline
last_updated: 2026-10-21
---

# Visual Analytics Architecture

> [Docs index](../README.md) › [02-architecture](README.md)

## 16.1 Dashboards

| Dashboard | Key visuals | Audience |
|---|---|---|
| **Production** | Tonnage rate `ṁ(t)` lines per node; yield by fraction (stacked area); plant graph with live utilisation colouring; circulating load | Site manager |
| **Conveyor & Bottleneck** | `Q_actual` vs `Q_nominal` with degradation band; utilisation heat strip by node | Site manager, maintenance |
| **Maintenance / Wear** | RUL trajectories with procurement threshold line (`T_lead + τ`); Gantt of upcoming replacements; grouping proposals | Planner |
| **Fuel** | Scatter of L/h vs t/h per asset with anomaly points highlighted; heatmap asset × day; reconciliation waterfall | Executive, controller |
| **Integrity** | Pending reviews, correction rates by controller, continuity exceptions, clock-skew flags, reversal rates | Executive, auditor |
| **Finance** | Net cash margin vs debt service per asset (lines) with rain-season shaded overlay; DSCR gauge; cost per tonne trend | CFO |
| **Inventory** | Stock value by warehouse; transfer flow (Sankey: Magasin Général → sites); variance history | Storekeeper, finance |
| **Sales & Dispatch** | Orders vs. delivered, weather-adjusted delivery forecast, tickets per hour | Commercial |

## 16.2 Engineering rules

- One **chart component library** (`@karry/charts`) wrapping ECharts with the design system palette, both light/dark and **outdoor high-contrast** mode.
- Server returns **pre-aggregated series** sized to the viewport (LTTB downsampling for long ranges); the client never downloads millions of points.
- Every chart supports: tooltip with units, export PNG/CSV, "explain this number" link to calculation detail, keyboard accessibility and a data-table fallback.
- Field tablets get lightweight variants (sparklines, KPI tiles) and cached last-known values offline.

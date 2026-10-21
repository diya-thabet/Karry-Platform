---
title: "ADR-0014: Apache ECharts as the primary charting library"
area: adr
tags: [charts, dashboards, frontend, echarts]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0014 — Apache ECharts as the primary charting library

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** [visual-analytics.md](../02-architecture/visual-analytics.md)

## Context

Dashboards show long time series (flow rates, telemetry), heatmaps and scatter plots with thousands of points, on low-end tablets and desktops. The specification proposed Recharts.

## Options considered

1. Recharts — **Pros:** Simple React API. **Cons:** SVG-based; slows beyond ~5k points; limited heatmaps.
2. Highcharts — **Pros:** Rich. **Cons:** Commercial licence.
3. **Apache ECharts** (chosen) + Recharts for small widgets — **Pros:** Canvas/WebGL rendering, large datasets, heatmaps, Sankey; Apache-2.0. **Cons:** Larger bundle (mitigated by modular imports).

## Decision

Use Apache ECharts via a shared `@karry/charts` wrapper (theme, units in axes/tooltips, export, data-table fallback, accessibility). Recharts may be used for simple small widgets. The server downsamples long ranges (LTTB) to viewport size.

## Consequences

- ✅ Performance headroom for production and telemetry charts.
- ⚠ Bundle size checked in CI; lazy-load chart modules.

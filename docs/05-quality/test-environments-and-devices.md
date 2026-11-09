---
title: "Test Environments, Data & Device Matrix"
area: quality
tags: [environments, test-data, devices]
source: "PHASES_DETAILED_PLAN.md §28"
status: baseline
last_updated: 2026-11-09
---

# Test Environments, Data & Device Matrix

> [Docs index](../README.md) › [05-quality](README.md)

## 28.1 Environments

| Environment | Data | Refresh | Used for |
|---|---|---|---|
| Local | Seeded synthetic | On demand | Dev |
| CI ephemeral (Testcontainers) | Per test | Per run | Integration tests |
| dev | Synthetic + demo generator | Continuous | QA functional |
| staging | Anonymised production snapshot + golden datasets | Weekly | UAT, performance, release candidates |
| eval tenant (AI) | Frozen dataset | Versioned | AI evaluation |
| prod | Real | — | Smoke tests only |

## 28.2 Test data

- **Golden datasets** (from P0, extended per phase): real anonymised shift sheets, fuel logs, tickets, maintenance history, plant configuration, finance schedules, with expected results computed by domain expert.
- **Synthetic generator**: configurable tenants (sites, assets, months of history) with seeded anomalies for detection rules. The first one, for shifts and meter readings, is [`tools/demo/generate_shifts.py`](../../tools/demo/generate_shifts.py) (P2.0; [testing strategy](testing-strategy.md#shift-data-generator-one-source-of-realistic-data)).
- **Anonymisation**: names, plates, customer names, prices scrambled; structure and distributions preserved.

## 28.3 Device matrix (field app)

| Device class | Example spec | OS / browser | Priority |
|---|---|---|---|
| Low-end Android tablet | 2 GB RAM, quad-core, 8–10" | Android 10–12, Chrome current & current-2 | P1 (reference device) |
| Rugged Android tablet | 4 GB RAM, IP65 | Android 12–14, Chrome | P1 |
| Mid-range Android phone | 4 GB RAM | Android 13–14, Chrome | P1 |
| iPad (office/weighbridge) | Any supported | iPadOS current-1, Safari | P2 (no Background Sync) |
| Windows/Linux desktop (weighbridge/office) | — | Chrome, Edge, Firefox current | P1 |
| Edge PC | x86 mini-PC 4 GB RAM | Debian 12 / Windows 10 IoT | P1 (from P6) |

Field conditions tested: direct sunlight, gloves, dust/water (rugged), low battery, 2G/3G throttling, no network for 72 h, device reboot mid-sync, storage almost full.

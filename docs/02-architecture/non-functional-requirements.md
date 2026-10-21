---
title: "Non-Functional Requirements"
area: architecture
tags: [nfr, sla, performance, offline, security]
source: "MASTER_PLAN.md §5"
status: baseline
last_updated: 2026-10-21
---

# Non-Functional Requirements

> [Docs index](../README.md) › [02-architecture](README.md)

| Category | Requirement |
|---|---|
| **Offline** | Field apps fully functional **≥ 72 h** without connectivity; ≥ 5,000 queued entries + photos per device without degradation. |
| **Device floor** | Usable on Android 10+, 2 GB RAM, Chrome/WebView ≥ last 2 years. First load ≤ 3 s on cached start, interaction ≤ 100 ms. |
| **Availability** | Cloud API SLO **99.9 %** monthly (MVP), 99.95 % at GA. Field capture is unaffected by cloud outages (offline-first). |
| **Performance** | p95 API latency < 300 ms for reads, < 500 ms for writes; dashboards < 2 s for 12 months of data per site. |
| **Scale (GA target)** | 500 tenants, 5,000 sites, 50,000 assets, 20 M field entries/year, 1 B telemetry points/year. |
| **Data integrity** | Zero silent data loss; every accepted entry is idempotent; audit chain verifiable end to end. |
| **Security** | OWASP ASVS **Level 2** baseline; MFA for privileged roles; encryption in transit (TLS 1.2+) and at rest; annual pen test. |
| **Privacy** | GDPR-aligned (data minimisation, DPA, right to access); location/photo data with explicit purpose; configurable retention. |
| **RPO / RTO** | RPO ≤ 5 min (PITR), RTO ≤ 4 h. Quarterly restore drills. |
| **i18n / l10n** | French & English at launch (Arabic RTL-ready architecture), locale-aware numbers/dates, metric + imperial units, multi-currency. |
| **Accessibility** | WCAG 2.2 AA for desktop apps; field UI: large touch targets (≥ 48 px), high-contrast outdoor mode, glove-friendly. |
| **Auditability** | Every state change attributable to user, device, time (device + server), location where captured. |
| **Maintainability** | Module boundaries enforced by architecture tests; ≥ 80 % coverage on domain/math code; ADRs for decisions. |

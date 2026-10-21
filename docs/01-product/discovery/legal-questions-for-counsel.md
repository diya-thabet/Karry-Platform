---
title: "Legal Questions for Counsel"
area: product
tags: [legal, e-signature, gdpr, retention, invoicing, contracts]
source: "Phase 0 WP-P0-06"
status: baseline
last_updated: 2026-10-21
---

# Legal Questions for Counsel

> [Docs index](../../README.md) › [01-product](../README.md) › [discovery](README.md)

Brief for the lawyer(s) in each target country. Their answers become the **legal memo** (WP-P0-06) and may change ADR-0007 and retention settings.

## Context to give counsel
Karry is a multi-tenant SaaS for quarries. Field staff sign records on tablets: a handwritten stroke image, plus an ECDSA signature by a device key bound to an enrolled device and an authenticated user, plus a server countersignature and optional RFC 3161 timestamp ([ADR-0007](../../adr/0007-signature-envelope.md)). Records are immutable; corrections are counter-signed reversals ([ADR-0006](../../adr/0006-immutable-facts.md)).

## Questions

**Electronic signatures**
1. Which legal category do these signatures fall into (simple / advanced / qualified, or the local equivalent)? Is that sufficient for shift reports, scale tickets, delivery notes, transfer waybills and stock-count reports?
2. Which documents (if any) legally require a qualified signature or paper original?
3. Is an RFC 3161 timestamp from a specific (accredited) authority required for evidential value?

**Scale tickets & invoicing**
4. Are weighbridge tickets subject to legal-metrology or tax rules (sequential numbering, no gaps, retention)? Is our offline **provisional numbering** with later renumbering acceptable?
5. Requirements for electronic invoices / delivery notes in the country (e-invoicing mandates, formats)?

**Data protection**
6. Applicable law (GDPR or local equivalent): our role (processor) vs customer (controller); required DPA clauses; cross-border transfer rules for hosting region and subprocessors (cloud, LLM provider).
7. Is GPS capture at the moment of signing, and photos of meters, lawful for employees? Which notices or consents are needed?
8. Rules on employee monitoring (fuel/hour anomaly detection naming individuals) and works-council or labour-law constraints.

**Retention & deletion**
9. Minimum and maximum retention periods for: tickets, invoices, shift/time records, fuel records, stock records, audit logs.
10. Is crypto-shredding of personal data while keeping hash-chained audit entries acceptable for erasure requests?

**Contracts & company**
11. Recommended contracting entity / country for selling SaaS in the target markets (affects billing provider and tax).
12. Review of MSA, ToS, SLA, DPA, reseller agreement templates.
13. Trademark availability for "Karry" in target markets (WP-P0-19).

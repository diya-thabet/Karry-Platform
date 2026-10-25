---
title: "Runbook: KarryTenantOverLimit"
area: operations
tags: [runbook, alert, billing, usage, limits]
source: "P1.10, US-P1-BIL-04 AC2, ADR-0023"
status: baseline
last_updated: 2026-10-25
---

# KarryTenantOverLimit

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** at the last daily usage snapshot, at least one company used more than its plan allows for one hour (`karry_billing_usage_over_limit > 0`, from the workers; severity `ticket`). The value is the number of tenant metrics above their limit (office users, assets).
- **Impact:** none for the customer yet. Limits are soft ([ADR-0023](../../adr/0023-plans-subscriptions-entitlements.md)): above the limit their administrators see a banner; at the limit + 10 % (at least one) only **creating** another counted item is refused (`billing.limit_reached`). Field capture is never blocked. For Karry it is revenue the subscription does not cover yet.
- **Diagnosis:**
  1. Which companies: the worker log, event **5002** (`Tenant {Tenant} is over its {Metric} limit: {Usage} of {Limit} ({State})`). The metric carries no tenant label on purpose.
  2. Their terms: `GET /api/v1/billing/tenants/{tenantId}/subscription` (operator token): plan, sites, extra assets, overrides.
  3. A count that looks wrong (for example office users counting people who left): ask the customer to deactivate them; invited and active office users each hold a seat, field users never do.
- **Mitigation:** contact the customer (account owner) about the right plan, more sites or extra assets; once agreed, change the terms with `PUT /api/v1/billing/tenants/{tenantId}/subscription`. The change applies at once on that API instance and everywhere within 60 s. A temporary `limitOverrides` entry is the escape hatch for a customer in the middle of a negotiation.
- **Resolution:** the next daily snapshot (02:30 UTC, or `Karry.Workers snapshot-usage`) shows the tenant within its limit and the alert clears.
- **Escalation:** ticket for the account owner within the working day; nothing to page for.

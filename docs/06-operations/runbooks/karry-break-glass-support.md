---
title: "Runbook: KarryBreakGlassSupport"
area: operations
tags: [runbook, alert, security, support-access, operator]
source: "P1.16, US-P1-OPS-03 AC3, ADR-0029 §4, threat T-14"
status: baseline
last_updated: 2026-10-29
---

# KarryBreakGlassSupport

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** a Karry platform operator opened a **break-glass** support session: read-only access to one company's data for one hour **without** a support code from that company (`karry_support_break_glass_sessions_total` went up on an API instance; severity `page`). The company's Tenant Admins were e-mailed at the same time.
- **Impact:** a person outside the company can read its data (costs stay masked) until the session ends. Legitimate in a production incident when the customer cannot be reached; otherwise it is a privileged-access incident (threat T-14).
- **Diagnosis:**
  1. Who, where, why: the API log, event **2200** (`Break-glass support session {Session} opened on tenant {Tenant} by operator {Operator}`). Events **2201** / **2202** say the Tenant Admins could **not** be e-mailed: then call the customer.
  2. The written reason and the exact times: the company's audit log, entry `identity.support_session_started.v1` (mode `break_glass`, operator, reason, end), or `GET /api/v1/identity/tenants/{tenantId}/support-sessions` with an operator token.
  3. Is there an open incident or ticket that matches the reason? Ask the operator directly.
- **Mitigation:** if the access is not justified, end it at once: the company's Tenant Admin with **Settings › Support access › End now**, or the operator with `POST /api/v1/identity/tenants/{tenantId}/support-sessions/{id}/end`. Then disable the operator's Keycloak account (platform realm) until reviewed.
- **Resolution:** a second person (security owner) confirms the reason against the incident within the working day and notes it in the incident record. The alert clears on its own after 15 minutes.
- **Escalation:** unjustified access → security incident procedure (the customer is informed in writing; the session's ledger entries are the evidence).

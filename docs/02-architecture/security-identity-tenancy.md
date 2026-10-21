---
title: "Security, Identity & Multi-Tenancy"
area: architecture
tags: [security, rbac, rls, tenancy, keycloak, threats]
source: "MASTER_PLAN.md §14"
status: baseline
last_updated: 2026-10-21
---

# Security, Identity & Multi-Tenancy

> [Docs index](../README.md) › [02-architecture](README.md)

## 14.1 Identity

- Keycloak with OIDC Authorization Code + PKCE for SPAs; refresh tokens rotated; short-lived access tokens (5–10 min).
- **MFA mandatory** for admin, finance, controller roles; optional for operators (device enrollment + PIN covers them).
- Enterprise tenants: SSO federation (Entra ID, Google Workspace, SAML).
- Offline auth: on enrolled device, a locally cached, time-boxed **offline session** (e.g., 72 h) unlocked by user PIN; all offline actions are re-validated server-side on sync.

## 14.2 Authorization model

```
Permission = (resource, action, scope)        e.g. (FuelTransaction, Approve, Site:Quarry-A)
Role       = set of permissions + field rules
FieldRule  = (resource.field, READ | WRITE | MASKED | HIDDEN)
```

- **RBAC** for coarse roles, **ABAC** for scope (tenant → site → warehouse), **field-level masking** applied in the API serialisation layer (never only in UI).
- Default roles shipped (Operator, Weighmaster, Controller, Storekeeper, Planner, Site Manager, Finance, Executive, Tenant Admin) and fully customisable.
- Permission changes are themselves ledger events.

## 14.3 Threat model highlights (STRIDE)

| Threat | Mitigation |
|---|---|
| Operator backdates / fabricates readings | Device signature, server receive time, clock-skew flag, mandatory photo with EXIF + in-app capture only, GPS geofence check |
| Controller colludes with operator | SoD, four-eyes on large corrections, executive anomaly dashboard, AI anomaly review |
| Admin tampers with DB | Append-only ledger + external anchors, DB audit logging (pgaudit), break-glass access with alerts |
| Cross-tenant data leak | RLS + query filters + tenant claims + automated cross-tenant tests in CI |
| Stolen tablet | Device revocation, local encryption, PIN, short offline session |
| Prompt injection via user-supplied text into AI | Tool-based access with RBAC, content treated as data, output filtering, no write tools for AI in v1 |
| Weighbridge indicator spoofing | mTLS edge agent, signed readings from edge, physical tamper seals noted in procedures |

## 14.4 Multi-tenancy implementation

- **Pool model** (shared DB, `tenant_id` column) for standard tenants; **silo option** (dedicated DB / dedicated cluster / on-prem) for enterprise/regulatory tenants — same codebase, configured per tenant in a tenant catalog.
- PostgreSQL:

```sql
ALTER TABLE fieldops.shift_entry ENABLE ROW LEVEL SECURITY;
ALTER TABLE fieldops.shift_entry FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON fieldops.shift_entry
  USING (tenant_id = current_setting('app.tenant_id', true)::uuid)
  WITH CHECK (tenant_id = current_setting('app.tenant_id', true)::uuid);
```

- The API opens a transaction per request and executes `SET LOCAL app.tenant_id = '<from validated token>'` (safe with connection pooling because `SET LOCAL` is transaction-scoped).
- App connects as a **non-owner role** without `BYPASSRLS`. Migrations run as a separate owner role.
- EF Core **global query filters** on `tenant_id` as a second layer; CI includes a test suite that attempts cross-tenant reads on every endpoint.

## 14.5 Data protection

- TLS everywhere; AES-256 at rest (managed disks + object storage SSE-KMS).
- PII minimisation: operators identified by employee ID; photos are of meters, not faces (UX guidance + optional face blur later).
- Data Processing Agreement template; subprocessor list (cloud, LLM provider) disclosed; per-tenant toggle to **disable external LLM** usage.
- Region selection per tenant (EU / Africa / Americas) as the platform grows.

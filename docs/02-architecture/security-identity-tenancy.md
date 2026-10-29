---
title: "Security, Identity & Multi-Tenancy"
area: architecture
tags: [security, rbac, rls, tenancy, keycloak, threats]
source: "MASTER_PLAN.md §14"
status: baseline
last_updated: 2026-10-29
---

# Security, Identity & Multi-Tenancy

> [Docs index](../README.md) › [02-architecture](README.md)

## 14.1 Identity

- Keycloak with OIDC Authorization Code + PKCE for SPAs; refresh tokens rotated; short-lived access tokens (5–10 min).
- **MFA mandatory** for admin, finance, controller roles; optional for operators (device enrollment + PIN covers them).
- Enterprise tenants: SSO federation (Entra ID, Google Workspace, SAML).
- Offline auth: on enrolled device, a locally cached, time-boxed **offline session** (e.g., 72 h) unlocked by user PIN; all offline actions are re-validated server-side on sync.

### 14.1.1 As implemented (P1.8, `deploy/keycloak`)

- **One realm `karry` with Keycloak Organizations**; one organization per tenant. A dedicated realm for Enterprise
  tenants stays possible later ([ADR-0012](../adr/0012-identity-authorization.md)).
- The tenant of a user is the **admin-only** user attribute `tenant_id`, mapped to the `tenant_id` access-token claim
  that `TenantResolutionMiddleware` reads; platform operators have `platform_role=operator` and no tenant. Users can
  neither read nor change these attributes (user profile permissions).
- App client `karry-portal` (the field app has none since P1.15: tablets sign their requests as enrolled devices, [ADR-0028](../adr/0028-field-devices-offline-data-shared-tablet.md)): public, Authorization Code + PKCE S256 only (no password grant, no
  implicit flow), explicit redirect URIs, audience `karry-api`, no Keycloak roles in tokens.
- Each company's portal address is added to `karry-portal` (redirect URI, web origin, post-logout URI) when the
  company is set up. Keycloak replaces a client whole and has no version check, so these read-modify-writes are
  serialized: one lock per realm inside a process, and a PostgreSQL advisory lock on the application database between
  processes (API host, workers, replicas). Without it, two companies set up at once could lose one address and its
  people could not sign in.
- Access token 5 min; refresh rotation with reuse 0: a reused refresh token revokes the app session (all its tokens).
  The Keycloak SSO cookie itself survives, which is safe: it is HttpOnly on the Keycloak origin and never in a token.
- Brute force: temporary lockout after 5 failures (growing wait, max 15 min), same message as a wrong password.
- MFA: realm role `mfa-required` (set by the Identity module for privileged roles) makes the browser flow ask for
  WebAuthn or TOTP, and forces TOTP enrolment before any token when the user has neither.
- **Apps** (`@karry/auth`, oidc-client-ts): Authorization Code + PKCE; access, refresh and ID tokens live **in memory
- **Step-up in the apps (P1.9):** on 401 `auth.step_up_required` the portal opens a sign-in pop-up (`max_age=0`, `prompt=login`; `@karry/auth` `stepUp()`, callback at `/auth/popup`) and retries the request once; a closed pop-up leaves the change unsaved and shows the error. Masked values render as `•••` with a lock (`MaskedValue` / `SensitiveValue` in `@karry/ui`), never as an empty field.
  only** (never localStorage/sessionStorage; asserted by the browser E2E). Only the sign-in state (PKCE verifier,
  return path) is in sessionStorage during the redirect and removed on return. A reload re-signs in through the
  Keycloak session cookie (one redirect, no form); a failed refresh (reuse detected, deactivated account) goes back to
  Keycloak. The return path is same-app only (no open redirect). `ui_locales` follows the app language.
- **Users** live in the Identity module (`identity.users`, RLS): office users linked to Keycloak by `sub`, field users
  with employee ID + PIN (PBKDF2-HMAC-SHA256, 600 000 iterations, salted; trivial PINs refused; never returned).
- **Invitations**: 7 days, single use, only the SHA-256 of the token stored; the link carries the token in the URL
  fragment (never sent to a server or logged); the token starts with the tenant ID so the anonymous accept runs under
  that tenant's RLS. The password typed on acceptance goes straight to Keycloak's admin API (policy enforced there) and is
  never stored or logged by Karry.
- **Deactivation** (TC-P1-IAM-007): Karry first (the tenant middleware asks `IAccessRevocation` on every tenant request;
  cached 30 s per instance, forgotten at once by the deactivating instance), then Keycloak (account disabled, sessions
  ended). A still-valid access token gets 401 `auth.revoked` within 30 s on any instance; the user record and name stay.
- **One realm, one account per e-mail** across tenants: inviting an address that already has an account elsewhere is a
  409 `identity.account_exists` (dedicated realm for Enterprise tenants that need it).
- **Failed sign-ins and lockouts** (US-P1-IAM-01 AC4): `Karry.Workers` reads Keycloak's `LOGIN_ERROR` events every minute and records each failure of a known user once (Keycloak's event ID is the key) in `identity.sign_in_failures` and the tenant's ledger (`identity.sign_in_failed.v1`, `locked` when the brute-force protection refused the attempt). Failures for unknown usernames belong to no tenant and are only counted.
- **Ledger events of identity changes carry identifiers only** (user, invitation, roles), never names, e-mails or phone
  numbers: the ledger is immutable, personal data must stay erasable.
- Details, flow diagram and the reasons for each value: [deploy/keycloak/README.md](../../deploy/keycloak/README.md).

**Security review checklist for any change to `deploy/keycloak`** (two-reviewer rule, P1.8):

| # | Check | Enforced by |
|---|---|---|
| 1 | Access token ≤ 10 min; refresh rotation on, max reuse 0 | `RealmConfigurationTests`, `KeycloakLoginTests` |
| 2 | App clients public, PKCE S256, no password grant, no implicit, no wildcard host | both |
| 3 | `tenant_id` / `platform_role` admin-only, mapped to exactly these claim names | both + `ApiTokenValidationTests` |
| 4 | Brute-force on, temporary only, generic error message | both |
| 5 | Registration off; password policy ≥ 12 characters | `RealmConfigurationTests` |
| 6 | `mfa-required` users cannot get a token without a second factor | `KeycloakLoginTests` (TC-P1-IAM-002) |
| 7 | No credentials in the realm file; client secrets from the environment | `RealmConfigurationTests` |
| 8 | Theme scripts only read public data, write with `textContent`, store no token | review |
| 9 | Image version identical in compose and tests | `RealmConfigurationTests` |

## 14.2 Authorization model

```
Permission = (resource, action, scope)        e.g. (FuelTransaction, Approve, Site:Quarry-A)
Role       = set of permissions + field rules
FieldRule  = (resource.field, READ | WRITE | MASKED | HIDDEN)
```

- **RBAC** for coarse roles, **ABAC** for scope (tenant → site → warehouse), **field-level masking** applied in the API serialisation layer (never only in UI).
- Default roles shipped (Operator, Weighmaster, Controller, Storekeeper, Planner, Site Manager, Finance, Executive, Tenant Admin) and fully customisable.
- Permission changes are themselves ledger events (`identity.role_created.v1`, `role_changed.v1`, `role_deleted.v1`, `field_levels_changed.v1`, with before and after).
- How the engine is built (declarations in code, fail-closed middleware, scope rules, step-up, masking in the JSON contract): [ADR-0022](../adr/0022-authorization-engine.md).
- Masking is proven by the `ci / authorization` job: the masking suite (TC-P1-IAM-005) checks that every money-like property of the committed contract is marked, that every success response is JSON (so it passes the masking serializer) and, for every API type with sensitive fields, that the host's own serializer never writes a masked or hidden canary value.

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
  USING      (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);
```

- **`nullif(..., '')` is mandatory**: once a session has used a transaction-local setting, `current_setting(..., true)` returns `''` (not `NULL`) and `''::uuid` raises an error instead of failing closed. Proven by spike WP-P0-12 ([report](../../spikes/rls-pooling/README.md), TC-P0-RLS-004).
- The API opens a transaction per request and runs `SELECT set_config('app.tenant_id', $1, true)` (the parameterized equivalent of `SET LOCAL`, transaction-scoped and therefore safe with connection pooling) **batched with the first statement** to avoid an extra round trip (spike measured +14 % latency for a separate round trip vs +2.4–4.0 % for RLS itself). Session-level `SET` is forbidden: the spike reproduced a cross-tenant leak with it (TC-P0-RLS-005).
- App connects as a **non-owner role** without `BYPASSRLS`. Migrations run as a separate owner role.
- EF Core **global query filters** on `tenant_id` as a second layer; CI includes a test suite that attempts cross-tenant reads on every endpoint.

### 14.4.1 As implemented (P1.5, `Karry.BuildingBlocks/Tenancy`)

| Layer | Implementation | Proven by |
|---|---|---|
| Roles | [`deploy/database/roles.sql`](../../deploy/database/roles.sql): `karry_owner` (schemas, tables, migrations via `Karry.Host migrate`), `karry_app` (DML only, owns nothing, no `BYPASSRLS`) | TC-P1-TEN-008 |
| Policy | `migrationBuilder.EnableTenantRls(schema, table)` → ENABLE + FORCE + `tenant_isolation` policy (USING = WITH CHECK, `nullif` form). `allowPlatformRows: true` variant (`tenant_id IS NOT DISTINCT FROM …`) for tables that also hold no-tenant rows (`platform.idempotency_records`) | catalog check test; FORCE, nullif, WITH CHECK mutation-checked |
| Tenant per transaction | `TenantCommandInterceptor`: command **outside** a transaction → `SET LOCAL app.tenant_id = '<uuid>';` prepended in the same batch (one round trip, implicit transaction); command **inside** a transaction → `SET LOCAL` once as the transaction's own statement. Every `SaveChanges` is transactional (`AutoTransactionBehavior.Always`). | TC-P1-TEN-001, pooled-session reuse with `No Reset On Close` (PgBouncer-like) |
| Tenant source | `TenantResolutionMiddleware`: exactly one UUID `tenant_id` claim in the **validated** token; otherwise 401/403 before the endpoint runs (no database connection opened). Module endpoints require authentication and a tenant by default (`MapModules`); opt-out only with `.AllowAnonymous()` / `.AllowWithoutTenant()`. Only exception: a platform operator's token in a running support session of their own (`Karry-Support-Session: <tenantId>.<sessionId>`, read only, fields masked, every request ledgered, [ADR-0029 §4](../adr/0029-operator-console-support-access-audit-viewer.md#4-support-access-a-ledgered-read-only-one-hour-support-session)) | TC-P1-TEN-003, TC-P1-OPS-004 |
| Tenant address | `<slug>.<Tenancy:BaseDomain>` must match the token's tenant (`ITenantSlugLookup`, Tenancy module, 1-minute cache); unknown slug refused; platform names (`api`, `www`…) not checked | TC-P1-TEN-009 |
| Operators | Provisioning requires policy `platform-operator` (claim `platform_role=operator`, provisional until roles in P1.8/P1.9) | Tenancy API tests |
| EF filter | Every `ITenantOwned` entity gets a query filter on the scope's tenant (no tenant → matches nothing) | layer tested alone (admin connection bypassing RLS) |
| Save check | `ModuleDbContext.SaveChangesAsync` refuses added/modified rows of another tenant before reaching the database | test + mutation |
| Events | Outbox rows carry `tenant_id`; the dispatcher runs each message's subscribers in that tenant | subscriber test |

Why `SET LOCAL` and not `SELECT set_config($1, …, true)`: `set_config` returns a result set, which would become the first result of every EF reader. The value is a .NET `Guid` formatted as `D` (hex digits and dashes only), so the statement cannot carry SQL. Why not prefix commands inside transactions: EF Core reads rows-affected per statement of a modification batch, and an extra leading statement shifted them (every insert reported 0 rows; found by the tests).

## 14.5 Data protection

- TLS everywhere; AES-256 at rest (managed disks + object storage SSE-KMS).
- PII minimisation: operators identified by employee ID; photos are of meters, not faces (UX guidance + optional face blur later).
- Data Processing Agreement template; subprocessor list (cloud, LLM provider) disclosed; per-tenant toggle to **disable external LLM** usage.
- Region selection per tenant (EU / Africa / Americas) as the platform grows.

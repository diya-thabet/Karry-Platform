---
title: "API Design Standards"
area: architecture
tags: [api, rest, openapi, errors, idempotency]
source: "MASTER_PLAN.md §18"
status: baseline
last_updated: 2026-10-24
---

# API Design Standards

> [Docs index](../README.md) › [02-architecture](README.md)

- **REST + JSON**, resource-oriented, `/api/v1/...`; OpenAPI 3.1 generated from code; TypeScript client generated (no hand-written fetch code).
- **Errors:** RFC 9457 Problem Details with stable `type` URIs and localisable `code`.
- **Idempotency:** `Idempotency-Key` header required on all POSTs that create facts.
- **Pagination:** cursor-based (`?cursor=&limit=`), max 200.
- **Filtering/sorting:** whitelisted fields only.
- **Concurrency:** `ETag` / `If-Match` on mutable master data.
- **Versioning:** URL major version; additive changes only within a version; deprecation headers + 6-month sunset.
- **Units in API:** quantities always as `{ "value": 12.5, "unit": "T", "state": "LOOSE" }` — never bare numbers.
- **Time:** ISO 8601 with offset; server stores UTC.
- **Security:** OAuth 2.1 bearer tokens; scopes per client type; rate limits per tenant & user; request size limits; audit log for all admin endpoints.

## Implemented conventions (P1.3, `Karry.BuildingBlocks/Api`, `…/Idempotency`)

**Error responses** (`application/problem+json`, RFC 9457). Every error has `type` = `https://karry.app/problems/{code}`, `title`, `status`, a stable machine `code` and a `traceId` for support; validation errors add `errors` (field → messages).

| `Error` kind | HTTP | Example code |
|---|---|---|
| Validation | 400 | `request.invalid` (with `errors`) |
| Forbidden | 403 | `asset.forbidden` |
| NotFound | 404 | `asset.not_found` |
| Conflict | 409 | `asset.duplicate_code`, `idempotency.request_in_progress` |
| BusinessRule | 422 | `meter.backwards`, `idempotency.key_reused` |
| (unhandled exception) | 500 | `server.unexpected_error`: generic title, **no exception type, message or stack trace**; details only in logs with the trace ID (TC-P1-PLT-021) |

**Authentication and tenant (P1.5).** Module endpoints (`/api/v1/{module}/…`) require a bearer token and a tenant **by default**. The tenant comes only from the validated token's `tenant_id` claim (exactly one UUID), never from a header, path or body. No or invalid token → **401**; a valid token without exactly one tenant → **403 `tenant.missing`**. Both are answered before the endpoint runs. An endpoint opts out explicitly with `.AllowAnonymous()` (public) and/or `.AllowWithoutTenant()` (platform operator, branding lookup), and the opt-out is reviewed.

**Tenant address (P1.5).** When `Tenancy:BaseDomain` is set (e.g. `karry.app`), a request on `https://<slug>.karry.app` must carry a token of *that* tenant, otherwise **403 `tenant.host_mismatch`** (also for an unknown slug). Platform addresses (`api`, `www` and the other reserved names, multi-label hosts) are not tenant addresses and are not checked.

**Authorization (P1.9, [ADR-0022](../adr/0022-authorization-engine.md)).** Every tenant endpoint declares a permission (`module.resource.action`) or is a reviewed "any member" endpoint; an undeclared endpoint answers 403 `auth.permission_undeclared`. A caller without the permission gets 403 `auth.forbidden` (extension `permission`). An object outside the caller's scope (sites, warehouses) answers **404**, like a missing one. A privileged change without a sign-in of the last 5 minutes gets 401 `auth.step_up_required` with `WWW-Authenticate: Bearer error="insufficient_user_authentication", max_age=300` (RFC 9470); the client re-authenticates and retries.

**Identity endpoints (P1.8, permissions P1.9).** Under `/api/v1/identity`: `POST /invitations` (`identity.users.manage`, `Idempotency-Key`; 201 with `userId`, `invitationId`, `expiresAt`, `delivered`; 409 `identity.user_exists` / `identity.account_exists`); `POST /invitations/accept` (anonymous: the token is the credential and names the tenant; 204, 404 `identity.invitation_invalid` for any unknown token, 409 `identity.invitation_used`, 422 `identity.invitation_expired`, 400 `identity.password_rejected` from Keycloak's policy); `POST /field-users` (`identity.users.manage`; employee ID + PIN; the PIN is never returned); `POST /users/{id}/deactivate` (`identity.users.manage`; idempotent, 422 `identity.cannot_deactivate_self`); `GET /users` (`identity.users.read`; cursor pagination on the UUIDv7 key, filtered by the caller's sites); `GET /users/{id}` (`identity.users.read`; 404 outside the caller's sites); `GET /me` (any member). A token of a deactivated user gets 401 `auth.revoked` on every tenant endpoint. Giving roles is refused with 403 `identity.scope_exceeded` (outside the caller's sites) or `identity.escalation` (rights the caller lacks). `POST /api/v1/tenancy/tenants` accepts an optional `admin` (first Tenant Admin, invited by e-mail) and reports `adminInvitation`.

**Tenancy endpoints (P1.5).** `POST /api/v1/tenancy/tenants` (platform operator, `Idempotency-Key` required; 201 created, **200 for the same slug and name again**, 409 `tenant.slug_taken`, 400 `tenant.slug_invalid` / `tenant.slug_reserved` / `tenant.name_invalid`); `GET /api/v1/tenancy/current` (the caller's company); `GET /api/v1/tenancy/public/{slug}` (anonymous branding for the login page, 404 `tenant.not_found`).

**Audit endpoints (P1.6).** `POST /api/v1/audit/verify` recomputes the caller's tenant ledger and returns `{valid, entriesChecked, firstBrokenSequence, problem, headHash, verifiedAt}`.

**Correlation (P1.4, `Karry.BuildingBlocks/Observability`).** Every response, success or error, carries `X-Correlation-Id` (the W3C trace ID, 32 hex characters) and `traceparent`. The problem `traceId` is the same value, so the ID a user quotes from an error dialog finds the trace directly. A caller that sends `traceparent` (portal, field app) joins the same trace (TC-P1-OPS-005).

Framework errors use the same shape: unknown route `route.not_found`, malformed JSON `request.invalid`, 405/415 likewise. Codes are part of the contract: never rename one; add a new code instead.

**Validation:** request bodies are validated by FluentValidation before the handler (`.Validate<TRequest>()`); every request type needs a validator.

**Idempotency** (`.RequireIdempotencyKey()`): header `Idempotency-Key` (1–200 printable characters). Same key + same request → the stored response is replayed with `Idempotency-Replayed: true` (also for 4xx); same key + different request → 422 `idempotency.key_reused`; while the first is running → 409 `idempotency.request_in_progress`; missing key → 400 `idempotency.key_required`; 5xx responses are not stored (the client may retry). Records are kept 24 h; a reservation abandoned by a crashed process is released after 2 min. Scope: tenant + user from P1.5/P1.8.

**OpenAPI:** `/openapi/v1.json` (generated from code) in every environment except production. The contract is also generated at **build time** into [`src/backend/Karry.Host/openapi/v1.json`](../../src/backend/Karry.Host/openapi/v1.json) and committed: `ci / backend` fails if it is stale, `ci / openapi-diff` (oasdiff) fails on a **breaking change** against the base branch, and `ci / tenant-isolation` attacks every operation in it. Every endpoint needs `.WithName(...)` (operationId) and declares its problem responses (`.ProducesProblem(...)`). The frontend's typed client (`@karry/api-client`, openapi-typescript + openapi-fetch) is generated from the same file; CI fails when it is stale. The client throws every non-2xx as `ApiError` (status, problem `code`, correlation ID), attaches the access token, and adds an `Idempotency-Key` to every write unless the caller sets one.


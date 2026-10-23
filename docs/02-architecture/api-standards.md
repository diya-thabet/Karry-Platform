---
title: "API Design Standards"
area: architecture
tags: [api, rest, openapi, errors, idempotency]
source: "MASTER_PLAN.md §18"
status: baseline
last_updated: 2026-10-21
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

**Correlation (P1.4, `Karry.BuildingBlocks/Observability`).** Every response, success or error, carries `X-Correlation-Id` (the W3C trace ID, 32 hex characters) and `traceparent`. The problem `traceId` is the same value, so the ID a user quotes from an error dialog finds the trace directly. A caller that sends `traceparent` (portal, field app) joins the same trace (TC-P1-OPS-005).

Framework errors use the same shape: unknown route `route.not_found`, malformed JSON `request.invalid`, 405/415 likewise. Codes are part of the contract: never rename one; add a new code instead.

**Validation:** request bodies are validated by FluentValidation before the handler (`.Validate<TRequest>()`); every request type needs a validator.

**Idempotency** (`.RequireIdempotencyKey()`): header `Idempotency-Key` (1–200 printable characters). Same key + same request → the stored response is replayed with `Idempotency-Replayed: true` (also for 4xx); same key + different request → 422 `idempotency.key_reused`; while the first is running → 409 `idempotency.request_in_progress`; missing key → 400 `idempotency.key_required`; 5xx responses are not stored (the client may retry). Records are kept 24 h; a reservation abandoned by a crashed process is released after 2 min. Scope: tenant + user from P1.5/P1.8.

**OpenAPI:** `/openapi/v1.json` (generated from code) in every environment except production; CI diffs it from P1.5.


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

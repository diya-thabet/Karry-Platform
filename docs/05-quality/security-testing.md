---
title: "Security Testing: Headers, Page Policy, OWASP ZAP Baseline and Findings"
area: quality
tags: [security, zap, csp, security-headers, hsts, e2e, maplibre, p1-18, h-w1, h-w2]
source: "P1.18a hardening plan, slice 4 (2026-11-03)"
status: live
last_updated: 2026-11-03
---

# Security testing

> [Docs index](../README.md) › [05-quality](README.md) · [Hardening plan](../04-phases/phase-01-platform-foundation/hardening-plan.md) · [Load testing](load-testing.md) · [Threat model](../02-architecture/threat-model.md)

Web-facing protections and how they are proven. Authentication (tokens, device assertions, the realm) is in the
[hardening plan §2.3](../04-phases/phase-01-platform-foundation/hardening-plan.md); tenant isolation in its own suite.

## 1. Headers

**The API** (`Karry.BuildingBlocks/Api/SecurityHeaders.cs`, every answer, errors included). It serves JSON and images,
never a page, so it allows nothing:

| Header | Value | Why |
|---|---|---|
| `Content-Security-Policy` | `default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'` | An answer opened in a browser can run, load or frame nothing |
| `X-Content-Type-Options` | `nosniff` | A JSON answer is never read as HTML or script |
| `X-Frame-Options` | `DENY` | Older browsers: never framed |
| `Referrer-Policy` | `no-referrer` | — |
| `Cross-Origin-Opener-Policy` | `same-origin` | — |
| `Cache-Control` | `no-store` unless the endpoint sets its own (logos, photos) | Answers name people, prices and companies: never in a shared cache |
| `Strict-Transport-Security` | `max-age=31536000; includeSubDomains`, HTTPS answers outside Development | The scheme comes from the trusted proxy (`ReverseProxy:KnownNetworks`); never pinned for localhost |
| `Server` | removed | Kestrel does not name itself |

**The portal and field app** (`src/frontend/security-headers.ts`). The production build is served with these by
`vite preview`, which the browser E2E tests run against, and by the production web server (P1.17 copies them):

| Directive | Portal | Field app |
|---|---|---|
| `script-src` | `'self'` | `'self'` |
| `style-src` | `'self' 'unsafe-inline'` (finding W-2) | same |
| `connect-src` | `'self'`, Keycloak, the map's style host, error tracking | `'self'`, error tracking |
| `img-src` | `'self' data: blob:`, the map's host | `'self' data: blob:` |
| `worker-src` | `'self' blob:` (MapLibre) | `'self'` (service worker) |
| `frame-src` | Keycloak (silent renew fallback) | `'none'` |
| `frame-ancestors`, `object-src`, `base-uri` | `'none'`, `'none'`, `'self'` | same |
| `Permissions-Policy` | nothing | `camera=(self)` (meter photos) only |

The origins come from the same `VITE_` settings the build uses, so the policy follows the environment.

## 2. Tests

| Test | Proves |
|---|---|
| `SecurityHeadersTests` (BuildingBlocks, real Kestrel) | Every header on 200, 404, 409 and 500 answers; no `Server`; an endpoint's own caching kept; HSTS only on HTTPS outside Development |
| `securityHeaders.test.ts` (portal, Vitest) | What each app's policy allows; nothing for a missing setting; camera only for the field app |
| E2E guard (`e2e/tests/guard.ts`, every browser test) | Any CSP violation on our pages fails the test. It caught W-2. |
| `security-headers.spec.ts` (E2E) | Both apps and the real API (through the app's `/api`) send their headers |
| `map.spec.ts` (E2E) | The site map draws in the production build under the portal's policy (W-3) |
| ZAP baseline (`tests/security/zap.sh`, nightly `zap` job) | Passive scan of both apps and of every API operation (OpenAPI, safe mode). Fails on any high finding, or a medium one not accepted in `tests/security/zap-accepted.tsv` with its reason (`zap_summary.py`, unit-tested) |

**Running ZAP locally:** local stack, `Karry.Host` and both previews running (or `tools/e2e/run.sh tests/security/zap.sh`,
which starts them), then `tests/security/zap.sh`. The image is a dated weekly build pinned by digest (1.1 GB). Reports
and `summary.md` are in `tests/security/results/<time>/`.

## 3. Findings

| # | Finding | Effect | Fix |
|---|---|---|---|
| W-1 | No security header on any API answer or page | Answers could be framed, sniffed, cached by shared proxies; `Server: Kestrel` | §1 headers, tested on every status |
| W-2 | ZAP 10055: `style-src 'unsafe-inline'` | Medium | **Accepted after measuring.** With `style-src 'self'` the E2E guard caught violations on every page: Radix's scroll lock (`react-style-singleton`) and React insert `<style>` elements at run time, which a static site cannot cover with a nonce or a hash. Inline styles cannot run code; scripts stay `'self'` |
| W-3 | **The site map was broken in the production build.** MapLibre 6 starts its worker from a separate file at an address computed at run time, so Vite never shipped it: "Worker failed to load" (the dev server hid it) | Drawing a site's boundary failed in any built portal | The worker is imported with `?url` and given to `setWorkerUrl`; `map.spec.ts` draws a map from the built chunks |

ZAP's first run (2026-11-03, local): no high finding; the only medium was W-2; low and informational findings
(`Cross-Origin-Resource-Policy`/`-Embedder-Policy` missing, client errors from the API scan's unauthenticated calls)
are listed in the summary and not acted on: COEP would break the map tiles and Keycloak frame, CORP would break the
login page's branding logo, which Keycloak's origin reads.

## 4. Not covered yet

- **An active scan** (attacks) against a staging copy: P1.18b, never against shared environments without a window.
- **The production web server's headers** are P1.17's (it must send §1's page headers; a deploy smoke test checks them).

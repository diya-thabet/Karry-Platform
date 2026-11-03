---
title: "P1.18a Hardening Plan: Scenario Matrix, Slices and Evidence (Local and CI, Before the Cloud)"
area: phases
tags: [phase-1, p1-18, hardening, qa, load-test, k6, security, zap, resilience, rate-limiting, keycloak, threat-model, scenarios]
source: "Owner decision 2026-11-03: start P1.18 without a cloud provider"
status: live
last_updated: 2026-11-03
---

# P1.18a hardening plan

> [Docs index](../../README.md) › [04-phases](../README.md) › [Phase 1 plan](plan.md) · [Sub-phases](sub-phases.md) · [Tracker](tracker.md)

The owner has not chosen a cloud provider yet (P1.17 waits). On 2026-11-03 the owner decided to start P1.18 anyway,
split in two ([sub-phases](sub-phases.md#p118--hardening-non-functional-tests-demo--exit-gate-m1)):

- **P1.18a, now:** everything that runs on the local stack and in GitHub Actions: load and abuse tests, security
  scans, resilience scenarios, threat and identity reviews, the demo rehearsal, the exit review draft.
- **P1.18b, after P1.17:** what needs real environments: staging deploy and rollback, restore drill, managed database
  failover, pilot data in staging, the load and ZAP runs repeated against staging, and the M1 gate.

The owner also asked for a system "hardened and battle tested for many scenarios". This page is the scenario matrix
that turns that into tests, the order of the work, and the evidence. A scenario is ✅ only with a test that runs in CI
(on each PR or nightly) and that was seen failing when the protection is removed.

## 1. Principles

1. **Test the system as it runs**, not only its parts: real Keycloak, real PostgreSQL with row-level security, real
   signed device requests, real browsers. Unit tests stay, but every scenario below has at least one test at the
   level where the failure would really happen.
2. **Every finding becomes a test first**, then a fix (a red test proves the gap).
3. **Numbers, not impressions:** load tests have thresholds that fail the run (k6 `thresholds`), scans have a
   severity gate, resilience tests check recovery time.
4. **Nothing flaky:** a test that fails once in twenty runs is a bug of the test or of the system, fixed before merge
   ([guidelines §7.6](../../00-llm/llm_guidelines.md)).
5. **Low-RAM friendly:** the suites run one at a time on the owner's 14 GB machine; the heavy ones run nightly in CI.

## 2. Scenario matrix

Status: ✅ covered (test name or file) · 🟡 partly · ⬜ to do. "Existing" points to tests that were already there.

### 2.1 Load and performance

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-L1 | 500 tablets pull their reference data at once (shift start) | 0 errors; p95 < 800 ms; every tablet gets its own site's data | k6 `tests/load/devices.js` | ⬜ |
| H-L2 | **TC-P1-TEN-005:** 10,000 mixed requests from people of two companies | **0 rows of the other company**; p95 < 300 ms; < 0.1 % errors | k6 `tests/load/tenants.js` | ⬜ |
| H-L3 | After the load, the audit ledger still verifies | Integrity check green | `tests/load/run.sh` (verify at the end) | ⬜ |
| H-L4 | A spike: 0 → 300 users in 10 s | No 5xx; latency back to normal within 30 s | k6 scenario `spike` | ⬜ |

### 2.2 Abuse and denial of service (threat T-12)

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-A1 | One tablet floods the sync API | 429 with `Retry-After` past its limit; other tablets unaffected | rate-limit integration tests | ⬜ |
| H-A2 | One person floods the API | 429 per person; colleagues unaffected | rate-limit integration tests | ⬜ |
| H-A3 | Anonymous flood of enrolment and public branding | 429 per IP address | rate-limit integration tests | ⬜ |
| H-A4 | Oversized body (sync batch > 8 MB, JSON > 1 MB elsewhere) | 413, nothing stored | integration tests | 🟡 (batch limit exists) |
| H-A5 | A page size far above the maximum (`limit=1000000`) | Capped or 400, never a full table scan | integration tests | ⬜ |
| H-A6 | Deeply nested or malformed JSON | 400 problem details, no 500 | integration tests | ⬜ |

### 2.3 Authentication and identity

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-I1 | Expired, wrong-issuer, wrong-audience, unsigned (`alg: none`) tokens | 401 | Existing: `Karry.AuthorizationTests` | 🟡 (audit the list) |
| H-I2 | A token of company A on company B's address | 403 | Existing: tenant isolation suite | ✅ |
| H-I3 | A device assertion replayed, expired, for another method or path, from a revoked tablet | 401 / 403 | Existing: device authentication tests | 🟡 (audit) |
| H-I4 | The realm's security settings drift (brute force, token lifetimes, PKCE, no direct grants, password policy) | CI fails | realm policy test (`deploy/keycloak`) | ⬜ |
| H-I5 | Password guessing on the sign-in page | Account locked after 5 tries for up to 15 minutes | realm policy test + E2E | ⬜ |

### 2.4 Resilience

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-R1 | PostgreSQL stops, then comes back | Readiness 503 while it is down, the API answers again within 30 s without a restart | resilience script | ⬜ |
| H-R2 | Keycloak stops | Signed-in people keep working until their token expires (keys cached); sign-in shows an error page | resilience script | ⬜ |
| H-R3 | Valkey (cache) stops | API still answers (cache is optional) | resilience script | ⬜ |
| H-R4 | Object storage stops | Photo upload answers a clear error; nothing else breaks | resilience script | ⬜ |
| H-R5 | Karry.Workers stops, then comes back | Outbox messages wait and are all delivered once, in order | resilience script | ⬜ |

### 2.5 Concurrency and integrity

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-C1 | Two people save the same site at once | One wins, the other gets 409 and keeps their draft | integration tests | 🟡 (audit) |
| H-C2 | The same enrolment code used by two tablets at once | Exactly one enrols | integration test | ⬜ |
| H-C3 | The same sync batch sent twice at once | Each command stored once | Existing: sync tests | 🟡 (audit) |
| H-C4 | Ledger rows touched in the database by an insider | Verifier reports the break | Existing: Trust tests | ✅ |

### 2.6 Web security

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-W1 | OWASP ZAP baseline scan of the portal and API | No high finding; medium findings fixed or accepted with a reason | `tests/security/zap.sh`, nightly | ⬜ |
| H-W2 | Security headers on every answer (CSP, `X-Content-Type-Options`, `Referrer-Policy`, frame-ancestors) | Present | integration test | ⬜ |
| H-W3 | Every threat of the [threat model](../../02-architecture/threat-model.md) that Phase 1 claims is linked to a passing test | Traceability check | `tools/ci` check | ⬜ |

## 3. Slices (one PR each)

| Slice | Content | Status |
|---|---|---|
| 0 | This plan, the P1.18a/P1.18b split | 🟡 |
| 1 | Rate limiting (H-A1–A3) and request limits (H-A4–A6) | ⬜ |
| 2 | k6 load suite with signed tablets and real sign-in (H-L1–L4), nightly job | ⬜ |
| 3 | Realm policy test and Keycloak review (H-I4, H-I5), authentication audit (H-I1, H-I3) | ⬜ |
| 4 | Security headers and ZAP baseline (H-W1, H-W2) | ⬜ |
| 5 | Resilience scenarios (H-R1–R5) | ⬜ |
| 6 | Concurrency scenarios (H-C1–C3), threat traceability (H-W3) | ⬜ |
| 7 | Demo rehearsal, exit review draft | ⬜ |

## 4. Evidence

Each slice adds its results here (numbers, CI run links), and the [tracker](tracker.md) links them.

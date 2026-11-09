---
title: "P1.18a Hardening Plan: Scenario Matrix, Slices and Evidence (Local and CI, Before the Cloud)"
area: phases
tags: [phase-1, p1-18, hardening, qa, load-test, k6, security, zap, resilience, rate-limiting, keycloak, threat-model, scenarios]
source: "Owner decision 2026-11-03: start P1.18 without a cloud provider"
status: live
last_updated: 2026-11-09
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
| H-L1 | 500 tablets pull their reference data at shift start: all in one second (burst) and over a minute (shift) | 0 errors; every tablet gets only its own site; shift p95 < 800 ms; burst p95 recorded (alarm 3 s) | k6 `tests/load/devices.js`; owner's laptop: burst 751 ms, shift 27 ms, 0 errors; nightly gates on correctness only (runner: burst 5.4 s, shift 1.09 s, 0 errors) ([results](../../05-quality/load-testing.md#3-results)) | ✅ |
| H-L2 | **TC-P1-TEN-005:** 10,000 mixed requests from people of two companies | **0 rows of the other company**; p95 < 300 ms; < 0.1 % errors | k6 `tests/load/tenants.js`; laptop: 0 leaks, p95 33 ms; nightly: 0 leaks, p95 109 ms | ✅ |
| H-L3 | After the load, the audit ledger still verifies | Integrity check green | `tests/load/verify-ledgers.sh` (`karry-ledger verify` for every tenant with a ledger), nightly after the load suite; locally after the slice 2 runs: 10 ledgers intact, 4,834 entries for demo company A | ✅ |
| H-L4 | A spike: 0 → 300 users in 10 s | No 5xx; no leak | k6 `tenants.js` `spike`; laptop and nightly: 0 server errors, p95 6 ms (nightly: 751 answers were 429 from the company limit, as intended) | ✅ |

### 2.2 Abuse and denial of service (threat T-12)

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-A1 | One tablet floods the sync API | 429 with `Retry-After` past its limit; other tablets unaffected; forged assertions naming it do not use up its limit | `RateLimitTests` | ✅ |
| H-A2 | One person floods the API; one company floods the API | 429 per person and per company; colleagues and other companies unaffected | `RateLimitTests` | ✅ |
| H-A3 | Anonymous flood of enrolment and public branding; flood of bad tokens; spoofed `X-Forwarded-For` | 429 per IP address, before authentication for bad tokens; only a known load balancer names the address | `RateLimitTests`, `RateLimitWiringTests` (real host) | ✅ |
| H-A4 | Oversized body (sync batch > 8 MiB, JSON > 1 MiB elsewhere), declared or streamed | 413 `request.too_large` before the body is read, nothing stored | `RequestLimitTests` (Kestrel), `ContractAbuseTests` (every operation) | ✅ |
| H-A5 | A page size far above the maximum (`limit=1000000`, -1, 0, 2^31, `ten`) | Capped at 200 or 400, never a 500 | `ContractAbuseTests` (every list) | ✅ |
| H-A6 | Deeply nested, cut short, wrongly typed or invalid UTF-8 JSON; junk query values (SQL, 10 000 digits, control characters) | 400 problem details, no 500, no row changed | `RequestLimitTests`, `ContractAbuseTests` (every operation and query parameter) | ✅ |

| H-A7 | 500 tablets of a site refresh in step every five minutes after opening together at shift start | Each tablet draws its own moment within ±30 s; after a 429 it waits 60–90 s, never retries at once | field `App.test.tsx` (`nextRefreshMs`, busy server) | ✅ |
| H-A8 | The portal gets a 429 | The person reads "too many requests, wait a minute"; the query is not sent again at once | portal `App.test.tsx` | ✅ |

### 2.3 Authentication and identity

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-I1 | Expired, not yet valid, wrong-issuer, wrong-audience, unsigned (`alg: none`), re-signed (HS256 with the public key, RS512, PS256), tampered and garbage tokens; a token in the URL | 401 | `TokenValidationTests` (production rules, 18 cases); real Keycloak tokens in `ApiTokenValidationTests` (the Identity service token, a moved tenant, `alg: none`). **Fixed:** the API accepted a token 5 minutes past its expiry (framework default); now 30 s, RS256 only | ✅ |
| H-I2 | A token of company A on company B's address | 403 | Existing: tenant isolation suite | ✅ |
| H-I3 | A device assertion replayed, expired, for another method or path, from a revoked tablet; a forged header (`alg` none/ES384/HS256, other `typ`, the neighbour's `kid`), a DER signature; one captured assertion fired 20 times at once | 401; exactly one of the 20 gets through | `DeviceApiTests` (audit: replay, binding, lifetime, clock, tamper, other company, revoked were covered; header forgery and the replay race added) | ✅ |
| H-I4 | The realm's security settings drift (brute force, token lifetimes, PKCE, no direct grants or implicit flow on any client, no `offline_access`, service account only for Identity, OTP policy, mail settings without credentials, the reset flow) | CI fails | `RealmConfigurationTests` (static, 17) and `KeycloakLoginTests` (live); `upgrade_realm.py --check` on a fresh import | ✅ |
| H-I5 | Password guessing on the sign-in page | Account locked after 5 tries for up to 15 minutes | `RealmConfigurationTests` pins 5 and 900 s; `KeycloakLoginTests.Repeated_failures_lock_the_account_temporarily…` proves it live | ✅ |
| H-I6 | **A forgotten password used to take over a privileged account.** Keycloak's built-in reset flow asks no second factor and removes the OTP: with mail configured, the inbox alone was enough | The e-mail link, then (privileged users) the second factor or its enrolment, then the password; no factor is ever removed; the same answer for unknown and disabled accounts, and no mail | `KeycloakPasswordResetTests` (live, mail through Mailpit). Mutation check: with the built-in flow, 2 of 5 fail | ✅ |

### 2.4 Resilience

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-R1 | PostgreSQL stops, then comes back; or stops answering (frozen, network cut) | Readiness 503 within the 5 s probe; requests get 503 `server.unavailable` + `Retry-After`, not 500; the API answers again within 30 s without a restart | `DatabaseOutageTests` (real stop/start at the same port, and a paused server). **Finding R-1 fixed:** a frozen database held the readiness probe 60 s (now a 3 s budget) | ✅ |
| H-R2 | Keycloak stops | Signed-in people keep working until their token expires (keys cached); a token from an unknown key is a quick 401, never a 500; an API started during the outage trusts the realm again soon after it returns | `KeycloakOutageTests` (the API's real token settings against a stand-in realm stopped and restarted on a real socket). Sign-in itself is Keycloak's page: nothing of ours to test | ✅ |
| H-R3 | Valkey (cache) stops | API still answers (cache is optional) | Nothing to break today: no Karry code uses Valkey (it is in the local stack for later phases). **Re-test when the first cache arrives** | ✅ (n/a) |
| H-R4 | Object storage stops | Photo upload answers a clear error; nothing else breaks | `PhotosApiTests` (503 `server.unavailable` + `Retry-After`, assets and sites keep working, a change saved before the cut is not reported as failed), `ObjectStoreTests` (refused and frozen storage). **Finding R-2 fixed:** a stopped storage was a 500 after the SDK's 100 s timeout and four retries; **R-3 fixed:** removing the replaced photo after the save turned a saved change into an error | ✅ |
| H-R5 | Karry.Workers stops, then comes back | Outbox messages wait and are all delivered once, in order | Existing `OutboxTests` (held rows, concurrent dispatchers, order, retries); new: the database session dies in the middle of a delivery, which rolls back and is delivered once, in order, later | ✅ |

### 2.5 Concurrency and integrity

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-C1 | Two people save the same site at once | One wins, the other gets 409 and keeps their draft | **Finding C-1:** api-standards promised `ETag`/`If-Match` but no update had it: a later save silently overwrote an earlier one. Sites done: `SitesApiTests` (an old version refused and the newer change kept; 8 saves from one version at once, exactly 1 applied, mutation-checked: without the `xmin` check 7 "succeeded"; malformed `If-Match`), `RecordVersionHttpTests`, portal `sites.test.tsx` (the notice keeps the draft; "save mine" or "show theirs"). **Materials done (6b-1):** the same tests in `MaterialsApiTests`, plus **density versions saved at once for different dates are never lost** (they live in the material's row as JSON: without the `xmin` check, 6 saves answered 200 and only 1 was kept; the old code also ignored a failed save). **Finding C-2 (portal):** forms were keyed by `changedAt`, so a refetch (window focus after 30 s, a density saved on the same page) remounted the form and silently replaced what the person typed with someone else's change; now `useRecordDraft` (`app/recordDraft.ts`): follow a newer version that changed nothing the form edits, show it if the draft is untouched, otherwise keep the draft and the old version (save refused, conflict notice). Sites and materials moved to it; hook tests and portal tests mutation-checked. **Assets done (6b-1b):** the same tests in `AssetsApiTests`; status and move races answer 200 only for a saved version (the service ignored 4 failed saves: label, photo set, photo removal, and the shared save of change/move/status; `Meters.AddAsync` too); `PhotosApiTests`: 6 photos sent at once leave exactly the one the asset shows (the refused uploads remove their object), the same photo sent twice is not removed by the loser; portal machine form on `useRecordDraft` (its own move or status no longer wipes the typing). All mutation-checked. **Company settings done (6b-2a):** the same tests in `TenantSettingsApiTests`; **finding C-3:** the old logo was deleted by its own command outside the save's transaction, so a refused or failed save left the company pointing at a logo that no longer existed (forced interleaving test: 404 without the transaction); concurrent logo inserts are a 409, not a 500; portal company form on `useRecordDraft` (a logo upload no longer wipes the typing). **Subscription done (6b-2b):** finding C-4: the subscription already had a shadow `xmin` row version (P1.11) but no code caught its conflict, so two operator actions at once, or an action during the hourly lifecycle run, answered 500 (or counted the run as failed); the aggregate now carries `Version`, the store reports the conflict and the API answers 409; operator terms take `If-Match`; tests: old terms refused, actions at once answer 200/409/422 with one state per version (mutation-checked: a 200 for a refused save is caught 4/4), the lifecycle run's stale save refused through the store and the payment kept; portal operator terms form on `useRecordDraft`. **Roles done (6b-3):** `UserAccount`, `CustomRole` and the default-role settings carry `Version` on the shadow `xmin` they already had (no migration); their conflict is now `request.version_conflict` (was `identity.concurrent_change`); `If-Match` on user roles, role change, field levels and role deletion; tests: an old version of the person or the role refused and the newer change kept (a stale delete refused too, the role still there), 4 role changes from one version at once apply exactly 1, 6 first field-level changes of a default role at once apply exactly 1 (version `"0"` until then), mutation-checked; portal users and role forms on `useRecordDraft` (role permissions held as a sorted list: a `Set` compares as `{}` in JSON, so a concurrent permission change would have been missed). **Shifts and flags (6d), finding C-6:** shift patterns and non-working days (one row per date) and flag overrides (one row per key) are set without a version; set at the same moment by several people they answered **500** (unique violations on the patterns, days and the shift instances they generate, plus deadlocks between those inserts, which surfaced as 503). The stores now report the conflict (409), and every save that changes shift rows holds a per-company advisory lock in its transaction (`ShiftWrites.SaveChangesSerializedAsync`, used by the shift and site stores and the daily generator; generator event 7003); tests `Many_people_setting_the_same_pattern_or_day_at_once_never_get_a_server_error` (3 runs clean, 3 of 3 failed without the lock) and `Many_operators_setting_a_company_s_flag_at_once_never_get_a_server_error` (failed with 500 before). | ✅ |
| H-C2 | The same enrolment code used by two tablets at once | Exactly one enrols | `One_code_used_by_many_tablets_at_the_same_moment_enrols_exactly_one` (8 tablets, 1 created, the others 422 `devices.enrollment_expired`, one device listed); mutation-checked: without the enrolment's `xmin` row version, more than one tablet enrolled | ✅ |
| H-C3 | The same sync batch sent twice at once | Each command stored once | `SyncBatchesTests`: the same batch sent 6 times at once is stored once (one receipt accepted, the others duplicate or 409 `sync.batch_conflict`); different commands with the same numbers sent at once keep one upload's commands. **Finding C-5:** the second case answered **503**: the concurrent inserts deadlocked on the unique sequence index (40P01) and the store caught only unique violations; now one upload of a tablet stores at a time (`pg_advisory_xact_lock` in `InboxStore.TryAddAsync`), so the later one meets the stored rows and gets the 409 | ✅ |
| H-C4 | Ledger rows touched in the database by an insider | Verifier reports the break | Existing: Trust tests | ✅ |
| H-C5 | **Two Workers processes anchor the same ledger at once** (a rolling deploy starts the new one, whose start-up run anchors, while the old one runs) | One anchor per sequence; never a false "tampering detected" | Found 2026-11-03 on the local stack (two versions of anchor 125, the verifier rightly alarmed); fixed: a PostgreSQL advisory lock around the whole run (`PostgresAnchoringLock`); `Two_workers_anchoring_at_the_same_time_write_each_anchor_once` failed before (2 runs failed, a second version) and passes after | ✅ |

### 2.6 Web security

| ID | Scenario | Expected | Test | Status |
|---|---|---|---|---|
| H-W1 | OWASP ZAP baseline scan of the portal, field app and API | No high finding; medium findings fixed or accepted with a reason | `tests/security/zap.sh` + `zap_summary.py`, nightly `zap` job; first run: no high, one medium accepted after measuring (W-2) | ✅ |
| H-W2 | Security headers on every answer (CSP, `X-Content-Type-Options`, `Referrer-Policy`, frame-ancestors, HSTS, no `Server`) and a page policy for both apps | Present; no page breaks under the policy | `SecurityHeadersTests`, `securityHeaders.test.ts`, E2E guard on every browser test, `security-headers.spec.ts` | ✅ |
| H-W3 | The site map in the production build | Draws under the page policy | `map.spec.ts`. **Found broken** (MapLibre's worker file was never shipped, [W-3](../../05-quality/security-testing.md#3-findings)), fixed | ✅ |
| H-W3 | Every threat of the [threat model](../../02-architecture/threat-model.md) that Phase 1 claims is linked to a passing test | Traceability check | `tools/ci/threat_traceability.py` in the `tooling` job (with its tests): a claimed threat without a cited `TC-P1-…` case or `` `…Tests` `` class, or citing one no test names, fails the build; first run found 5 claims without evidence, now cited | ✅ |

## 3. Slices (one PR each)

| Slice | Content | Status |
|---|---|---|
| 0 | This plan, the P1.18a/P1.18b split | ✅ #123 |
| 1a | Rate limiting and the load balancer's address (H-A1–A3) | ✅ #124 |
| 1b | Request limits and the contract-wide abuse tests (H-A4–A6) | ✅ #125 |
| 1c | Clients: portal messages for 429 and 413, no immediate retry after 429; tablets wait 60–90 s after a 429 and spread their five-minute refreshes | ✅ #126 |
| 2 | k6 load suite with signed tablets and real sign-in (H-L1, L2, L4), nightly job; finding L-1 fixed (one connection pool per process) | ✅ #127 |
| 3 | Realm policy test and Keycloak review (H-I4, H-I5, H-I6), authentication audit (H-I1, H-I3); findings: the reset flow (H-I6) and the 5-minute token tolerance (H-I1), both fixed | ✅ #128 |
| 4 | Security headers and ZAP baseline (H-W1, H-W2, H-W3); findings: no headers (W-1), the production map's missing worker (W-3), the anchoring race (H-C5, fixed in its own PR) | ✅ #129 |
| 5 | Resilience scenarios (H-R1–R5), ledger after load (H-L3); findings R-1 (readiness hung 60 s), R-2 (storage outage was a slow 500), R-3 (saved photo change reported as failed), all fixed; outages answer 503 `server.unavailable` (D54) | ✅ #131 |
| 6 | Concurrency scenarios (H-C1–C3), threat traceability (H-W3); 6a: record versions (`ETag`/`If-Match`) on sites, finding C-1; 6b-1: materials (density race), finding C-2 (form drafts lost on refetch); 6b-1b: assets (photo race); 6b-2a: company settings, finding C-3 (logo outside the transaction); 6b-2b: subscription, finding C-4 (uncaught row-version conflict); 6b-3: roles and a person's roles; 6c: enrolment race (H-C2), sync batch races (H-C3), finding C-5 (deadlock answered 503); threat traceability check (H-W3); 6d: shift and flag races, finding C-6 | ✅ #138–#144 |
| 7 | Demo rehearsal, exit review draft: [exit review (draft)](exit-review.md), with the demo script checked step by step against its tests and the gaps to close before the recorded demo | ✅ |

## 4. Evidence

Each slice adds its results here (numbers, CI run links), and the [tracker](tracker.md) links them.

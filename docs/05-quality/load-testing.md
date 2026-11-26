---
title: "Load Testing: k6 Suite, Scenarios, Thresholds, Results and Findings"
area: quality
tags: [load-test, k6, performance, tenant-isolation, tc-p1-ten-005, devices, sync, rate-limits, connection-pool, p1-18]
source: "P1.18a hardening plan, slice 2 (2026-11-03)"
status: live
last_updated: 2026-11-03
---

# Load testing

> [Docs index](../README.md) › [05-quality](README.md) · [Hardening plan](../04-phases/phase-01-platform-foundation/hardening-plan.md) · [Testing strategy](testing-strategy.md)

The k6 suite in `tests/load/` loads the API the way production traffic does. People sign in through Keycloak
(Authorization Code + PKCE, the portal's client). Tablets are enrolled by a site manager and sign every request
with their own P-256 key (ADR-0028 §3). Every request goes through authentication, tenant resolution, row-level
security and the rate limits. Nothing is mocked and nothing is bypassed.

## 1. Scenarios and pass criteria

| Script | Scenario | Load | Pass (k6 thresholds; a crossed one fails the run) |
|---|---|---|---|
| `devices.js` | **burst** (H-L1) | 500 tablets of one site ask for their snapshot in the same second | 0 failed requests; all 500 answered; every tablet gets only its own site; p95 < 3 s (regression alarm, see §4) |
| `devices.js` | **shift** (H-L1) | The 500 tablets come in over 60 s, then pull deltas every 2–4 s for 60 s. The app does it every 5 min, so this is about 100 times the real rate. | 0 failed requests; p95 < 800 ms; at least 500 pulls |
| `tenants.js` | **mixed**: TC-P1-TEN-005 (H-L2) | 10,000 reads by 6 people of two companies (50 virtual users), 14 read endpoints | **0 cross-tenant leaks**; at least 10,000 answers searched; p95 < 300 ms; < 0.1 % failed |
| `tenants.js` | **spike** (H-L4) | 0 → 300 users in 10 s, held for 20 s, with think time | 0 server errors (a 429 is a correct answer); 0 leaks |
| `shift-change.js` | **shift change** (P2.12a, M2 gate) | 300 tablets of one site, each with its own machine (setup registers them), come back over 2 min and send 5 shift entries started and ended: 10 commands signed as the app signs them (the bundle of `@karry/crypto`, `lib/crypto.gen.js`), each with a ~200 KB photo and the handwritten signature (strokes, image) uploaded by pre-signed `PUT`, in batches of 5 | 0 failed requests and 0 5xx; all 3,000 commands stored at once (`accepted`, or kept with their integrity flags as `conflict`), none waiting, refused or duplicated; all 9,000 blobs uploaded; p95 batch < 1 s (a latency gate: reported on shared runners); then `outbox-drained.sh`: every module's outbox handled by Karry.Workers within 10 s, nothing dead-lettered |

**Leak search.** Setup collects each company's identifiers: tenant ID, slug, name, and the IDs of its sites,
materials and machines. Every answer to a person of the other company is searched for them. A positive control
proves the search sees a company's identifiers in its own answers, so "0 leaks" is a real result.

**No vacuous pass.** A rate threshold passes when it has no data, and the first run passed with 0 pulls. So each
scenario also counts its successful answers (`burst_pulls_ok`, `shift_pulls_ok`, `answers_checked`) with an exact
or minimum count. A snapshot must also contain the tablet's own site before the wrong-site counter is trusted.

## 2. Running it

```bash
tools/ci/install-tool.sh k6                    # pinned k6 2.3.0, checksum verified
# local stack up, migrated and seeded; the shift change's 300 machines bought for the demo companies (idempotent):
Demo__ExtraAssets=400 dotnet Karry.Host.dll seed-demo
# Karry.Host (Release) started with the load-run limits, and Karry.Workers:
RateLimits__PerPersonPerMinute=100000 RateLimits__AnonymousPerAddressPerMinute=100000 dotnet Karry.Host.dll --urls http://127.0.0.1:5000
tests/load/run.sh                              # all three runs, one at a time, 65 s apart; summaries in tests/load/results/<time>/
tests/load/verify-ledgers.sh                   # H-L3: every company's audit ledger still verifies
```

- **Limits during a load run.** Six demo accounts stand for many people, and setup enrols 500 tablets from one
  address, so the person and anonymous-address limits are raised. The tablet (120/min) and company (12,000/min)
  limits stay as in production, so the run also proves they do not refuse legitimate traffic.
- **Sizes.** `TABLETS`, `REQUESTS`, `VUS` and `SPIKE_VUS` change them. Small runs (for example `TABLETS=3`) check
  the scripts in seconds.
- **Token lifetime.** Access tokens live 5 minutes. `tenants.js` refuses to start when they would expire before
  the run ends, and `devices.js` signs in again during a long setup.
- **Clean-up.** The tablets are revoked in teardown. Revoked rows stay in the local database.
- **Nightly.** The `load` job of `.github/workflows/nightly.yml` brings the stack up as the `local-stack` job does,
  runs `tests/load/run.sh`, then `tests/load/verify-ledgers.sh` (even when a threshold failed), writes the table
  to the job summary and keeps the results for 14 days. It runs with `LATENCY_GATES=report`: it **fails on
  correctness** (failed requests, server errors, a leak or a wrong site, a broken ledger) and only **reports**
  response times. A shared GitHub runner (2 vCPU, k6 and the whole stack on it) is 7× slower than the owner's
  laptop, and its times vary with its neighbours, so they say nothing about Karry. The latency targets gate on known
  hardware: the owner's machine (`tests/load/run.sh` without the variable), then staging in P1.18b.
- **Runs are 65 s apart** (`PAUSE_SECONDS`): the company rate limit is a one-minute sliding window, and `devices.js`
  loads demo company A at about 7,500 requests a minute. Run back to back, the mixed run's 5,000 extra requests for
  that company crossed the 12,000 limit on a fast runner (2.4 % answered 429), and the spike's setup was refused
  ("too few identifiers to look for"): nightly 2026-11-08 and -09 failed this way with no code change. The limit was
  right; the suite now lets each window empty.
- **The owner's machine (14 GB).** k6 needs about 1.4 GB for 500 tablets. Run one script at a time, with nothing
  else heavy open.

## 3. Results

**2026-11-03, the owner's laptop** (12 cores, 14 GB; k6, PostgreSQL, Keycloak and the API on the same machine):

| Run | Requests | p95 | Failed | Leaks / wrong site |
|---|---|---|---|---|
| Burst: 500 snapshots in one second | 500 | 751 ms | 0 | 0 |
| Shift: 500 tablets, deltas every 2–4 s | 15,579 | 27 ms | 0 | 0 |
| TC-P1-TEN-005: two companies, mixed reads | 10,000 | 33 ms | 0 | **0** |
| Spike: 0 → 300 users in 10 s | 12,784 | 6 ms | 0 (0 server errors) | 0 |

A single warm snapshot takes about 17 ms (5 KB). In the burst, the time goes to queueing: 500 requests at once
wait for one of the 50 database connections.

**2026-11-04, nightly on a GitHub runner** (2 vCPU, 7 GB, everything on one machine; the first nightly run):

| Run | Requests | p95 | Failed | Leaks / wrong site |
|---|---|---|---|---|
| Burst: 500 snapshots in one second | 500 | 5.4 s | 0 | 0 |
| Shift: tablets, deltas every 2–4 s | 14,026 | 1.09 s | 0 | 0 |
| TC-P1-TEN-005: two companies, mixed reads | 10,000 | 109 ms | 0 | **0** |
| Spike: 0 → 300 users in 10 s | 12,894 | 6 ms | 751 refused with 429, 0 server errors | 0 |

Every request was answered correctly and both ledgers verified afterwards; only the latency targets set on the
laptop were crossed, which is why the nightly now reports times instead of gating on them. The spike's 429s are
the company limit doing its job: about 12,800 requests in 35 seconds from one company is over its 12,000 a
minute (on the laptop the run fell across two one-minute windows, so none showed). A 429 is a correct answer in the
spike; only a 5xx fails it.

P1.18b repeats the suite against staging on the chosen provider. Those numbers will be the reference for
production sizing; laptop numbers are a baseline.

## 4. Findings

| # | Finding | Effect | Fix |
|---|---|---|---|
| L-1 | **The API ran out of database connections.** Npgsql kept one pool per configuration in each process: the plain module contexts, the spatial Organization context (another plugin, another pool) and the host's own data source. Each could open 100 connections, and PostgreSQL allows 100 in total. | First 500-tablet run: **8.6 % of requests failed** with `53300 too many clients` / `remaining connection slots are reserved`; p95 4.2 s | `KarryDataSources`: **one `NpgsqlDataSource` per connection string per process**, shared by every module, idempotency and the health check, with the spatial mapping as a plugin. `Maximum Pool Size` in the connection string is now the process's real budget: locally the API has 50 and Workers 10, under PostgreSQL's 100. A burst waits for a free connection instead of being refused. The re-run had **0 failures** and connections stayed at 60. **P1.17:** size each process's pool so the total stays under the managed database's limit, and plan for PgBouncer when instances multiply. |
| L-2 | k6 2.3.0 exports P-256 JWK coordinates without their leading zero bytes, for about one key in 200. RFC 7518 §6.2.1.2 requires the full 32 bytes. | Enrolment refused with `devices.key_invalid` for those test keys. Karry is right to refuse; browsers pad correctly. | The k6 helper pads `x`, `y`, `d`. No change to the API. |
| L-3 | Thresholds on rates pass on no data. | The first run "passed" without a single pull. | Count of successful answers with an exact or minimum count in every scenario (§1). |
| L-4 | The sub-phase card asks for a 500-device **baseline** and sets no latency target. The plan's p95 < 800 ms for "500 at once" was set during P1.18a. The first measured burst was 1.65 s with three pulls per tablet back to back. | Needed a decision. | **Decided:** the realistic shift (tablets over a minute, about 100 times the real pull rate) must meet p95 < 800 ms. The burst (all in one second) must have 0 errors and 0 wrong-site, with a 3 s regression alarm. After L-1 the burst measured 751 ms, so it meets 800 ms today. |
| L-5 | **A tablet's batch held its company's ledger for its whole length** (P2.12a shift change, 2026-11-26). The batch is one transaction; the first command saved appended to the ledger, which locks the company's ledger head `FOR UPDATE` until the commit, while the next commands were still verified, checked by the integrity rules and saved. Measured locally: one tablet, a batch of 5 in 0.8 s; 20 tablets at once, 10–15 s each (20 × 0.8 s), with 6–7 of 8–11 database sessions waiting on `audit.ledger_heads`. At 300 tablets the batches would have queued for minutes. | Every upload of a company waited for the others: the shift change could not meet its p95 or its 15 minutes. | **Fixed:** `LedgerDeferral` (ADR-0008 amendment). The sync session marks its transaction; the ledger writer keeps the entries and the session appends them right before the commit, still in the same transaction, in order, following savepoints. 20 tablets: 2.5 s average, the ledger waits gone; what remains on a laptop is CPU. |
| L-6 | A shift entry stored with integrity flags is answered `conflict` (ADR-0031 §8), not `accepted`. With the demo site's few meters, tablets started entries on the same meter at once and every one was flagged. | The first runs counted them as failures. | One machine per tablet (registered in setup), as at a real shift change; `commands_stored` counts `accepted` and `conflict`; `flags_raised` reports the flags by name. |
| L-7 | The first nightly shift change stopped in setup at the 102nd machine: `billing.limit_reached`. The demo companies are on the trial plan, and 300 tablets each register a machine. | Nightly run of 26 Nov 2026: setup failed, 0 commands sent | `DemoSeed` buys machines beyond the plan when `Demo:ExtraAssets` is set, through the same subscription change as a customer (in the ledger), and the nightly `load` job reseeds with `Demo__ExtraAssets=400` before the run. The limit itself worked as designed. |

## 5. Not covered yet

- **Uploads at the shift change on staging** (P2.12b): `shift-change.js` runs nightly on a shared runner, where the
  latency gate is only reported.
- **Staging numbers** (P1.18b).

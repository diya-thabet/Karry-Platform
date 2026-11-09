---
title: "Quality Engineering & Testing Strategy"
area: quality
tags: [testing, qa, definition-of-done, property-tests, device-lab, usability, phase-2]
source: "MASTER_PLAN.md §20"
status: baseline
last_updated: 2026-11-09
---

# Quality Engineering & Testing Strategy

> [Docs index](../README.md) › [05-quality](README.md)

| Level | Tooling | Scope / Rules |
|---|---|---|
| Unit | xUnit, Vitest | All domain logic; ≥ 80 % coverage in Domain projects |
| **Property-based** | FsCheck | Every formula: unit round-trips (`t→m³→t`), mass conservation in plant graph (Σin = Σout at steady state), RUL monotonicity, DSCR invariants |
| **Mutation** | Stryker.NET | Math & trust modules, mutation score ≥ 70 % |
| **Golden datasets** | Domain-expert spreadsheets | Real quarry cases recomputed by engines must match to tolerance |
| Integration | Testcontainers (real PostgreSQL+extensions, Keycloak with Mailpit for its e-mails) | Repositories, RLS, migrations, outbox, sign-in and password-reset flows |
| **Tenant isolation** | Automated suite | Every endpoint called with tenant B token on tenant A data must 404/403 |
| Contract | OpenAPI diff in CI | Breaking change detection |
| E2E | Playwright | Critical journeys incl. **offline mode** (network emulation), multi-device sync, signature flow |
| Device lab | Real low-end Android tablets | Performance budgets, outdoor readability, battery |
| Load | k6 ([load testing](load-testing.md), `tests/load/`, nightly) | Sync bursts (shift change at 06:00 / 18:00 across all sites), mixed-tenant reads with a leak search (TC-P1-TEN-005), spikes; dashboards later |
| Security | ZAP (DAST), pen test before GA, ledger tamper red-team exercise | — |
| AI evals | Golden Q&A sets | Correct tool selection, numeric fidelity, citation presence, FR/EN parity |
| UAT | Pilot site with real crews | Signed off by domain lead before each phase exit |

**Definition of Done (per story):** code + tests + docs updated, i18n strings (FR/EN), permissions defined, audit events emitted, telemetry added, offline behaviour defined (if field-facing), accessibility checked, reviewed and merged, deployed to dev.

## Phase 2 additions (field operations, P2.0)

Phase 2 brings the first data typed by people in the field, offline, on tablets. Four kinds of test grow
from it. The targets they check are in the
[Phase 2 sub-phases §3.5](../04-phases/phase-02-field-operations/sub-phases.md#35-non-functional-budgets-checked-in-ci-or-nightly).

### Shift data generator: one source of realistic data

[`tools/demo/generate_shifts.py`](../../tools/demo/generate_shifts.py) writes N sites × M assets × D days of three
8-hour shifts, with meter readings that continue from one shift to the next, and a configurable share of anomalies.
The anomaly kinds are continuity gaps, negative and oversized deltas, clock skew, reused photos and missing ends.
Night shifts cross midnight, and meter replacements are recorded as events.

- Each entry says what a rule engine must find (`expected_flags`). It also lists the cases the engine must **not**
  flag (`edge_cases`: `crosses_midnight`, `meter_replacement`). So the engine is tested for both missed anomalies
  and false alarms.
- The output is **deterministic** from `--seed`. A test that fails writes its seed, so anyone can replay the run.
- The data feeds the continuity rule tests (P2.7), the shift-change load test (P2.12a), the 200-entry PDF test
  (P2.11) and the demo. Its own tests (same seed → same bytes; anomaly rates; every flag true of its entry) run in
  CI's `tooling` job.
- Readings are decimal strings with an explicit unit (`h`, `km`), never floating-point numbers.

### Property tests (FsCheck 3.6, from P2.1)

Each property runs at least **1 000 cases** in CI. FsCheck shrinks a failure to its smallest example and prints
the seed; the shrunk example then becomes an ordinary regression test.

| Property | Sub-phase |
|---|---|
| Continuity: an anomaly-free sequence raises no flag; each generated anomaly raises its own rule, and nothing else; a recorded meter replacement never raises RULE-FO-01 | P2.7 |
| Δ is unit-safe: a reading in `h` is never compared with one in `km`; a negative Δ is always RULE-FO-02 | P2.7 |
| Sync ordering: commands applied in any order of arrival give the same final state; resent commands and duplicates change nothing | P2.1 |
| Canonical JSON: `canonical(parse(canonical(x))) == canonical(x)`, and the envelope hash is stable across member order | P2.3 |
| Client/server validation agree: the shared test vectors give the same verdict in TypeScript and C# | P2.5 |

Mutation testing (Stryker.NET, score ≥ 70 %) covers the continuity engine and the envelope code, as for the trust
module.

### Device lab protocol (P2.12b, once the reference tablet exists)

- **Devices:** 3 low-end Android tablets (the reference device of the
  [device matrix](test-environments-and-devices.md#283-device-matrix-field-app)) and 1 mid-range phone, with the
  browser versions written down.
- **Script:** a full 12-hour shift per device: enrolment, 6 starts and ends with photos, a signature, 72 h in
  aeroplane mode with 400 entries and 300 photos, then reconnection on throttled 3G.
- **Measured:** time per step; battery used (< 15 % over 12 h); sync time after reconnection (< 15 min) and data
  lost (must be 0); readability in direct sunlight and with gloves; camera focus on a dirty meter.
- **Recorded** in a dated lab report under the phase folder, with each failure triaged by the
  [defect severity scale](defect-severity-and-triage.md). Until real tablets exist, CI uses Playwright with network
  emulation and CPU throttling. This is a stand-in, not a pass.

### Usability test protocol (P2.5 operators, P2.9 controllers)

- **Participants:** 3–5 operators (P2.5) and 2–3 controllers (P2.9). If the pilot site cannot spare them, use a
  proxy group of people who have never seen the app. The report says which it was.
- **Set-up:** a mid-range phone (and the low-end tablet when available), data from the generator, the person's own
  language, no help from the facilitator during a task.
- **Tasks (operators):** start a shift on an asset found by QR code; end it with a photo and a signature; correct
  a wrong reading before syncing. **Tasks (controllers):** review 50 unflagged entries; open and resolve a flagged
  one.
- **Targets:** median start of a shift ≤ 60 s, no failed task (operators); 50 entries reviewed in < 5 min
  (controllers).
- **Recorded:** time per task, errors, the person's own words, and a severity per finding. Findings that block a
  task are fixed before the next sub-phase starts (P2.6 for operators).


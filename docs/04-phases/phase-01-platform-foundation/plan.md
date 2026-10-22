---
title: "Phase 1 — Platform Foundation & SaaS Core"
area: phases
tags: [phase-1, tenancy, iam, billing, ledger, pwa]
source: "PHASES_DETAILED_PLAN.md §8"
status: baseline
last_updated: 2026-10-21
---

# Phase 1 — Platform Foundation & SaaS Core

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 5 weeks (W4–W8), 2.5 sprints + hardening · **Team:** full core team

**Execution order:** this phase is built in 19 sub-phases (P1.0 – P1.18), described in [sub-phases.md](sub-phases.md), with live status in the [Phase 1 tracker](tracker.md).

## 8.1 Objectives & business value

Build the **production-grade skeleton** that every feature plugs into. That includes the SaaS foundations (tenants, plans, entitlements, users, roles) so that from P3 we can **sell and bill** without rework.

## 8.2 Scope

**In:** repository & CI/CD, environments & IaC, observability, identity (Keycloak), tenancy & RLS, entitlements & plans (manual billing), users/roles/permissions/field masking, organization & sites, assets & meters master data, materials & units engine, Excel import, audit ledger core, PWA shell with offline reference data & device enrollment, design system, i18n FR/EN, platform operator console v1.
**Out:** field transactions (P2), billing provider integration and self-service checkout (P10), SSO federation (P9/P10).

## 8.3 Epics

| Epic | Title | Outcome |
|---|---|---|
| EP-P1-01 | Engineering platform | Repo, CI/CD, environments, IaC, quality gates |
| EP-P1-02 | Observability baseline | Traces, metrics, logs, alerts, error tracking |
| EP-P1-03 | Identity & authentication | Keycloak, OIDC login, MFA, sessions |
| EP-P1-04 | Tenancy & isolation | Tenant catalog, provisioning, RLS, isolation tests |
| EP-P1-05 | Plans & entitlements | Plans, subscriptions (manual), feature gates, limits, usage meters |
| EP-P1-06 | Authorization (RBAC/ABAC/masking) | Roles, permissions, scopes, field rules, admin UI |
| EP-P1-07 | Organization & sites | Company, sites, timezones, shift patterns, calendars |
| EP-P1-08 | Assets & meters | Asset registry, types, meters, materials, densities |
| EP-P1-09 | Units engine | Conversions with material state, property-tested |
| EP-P1-10 | Data import | Excel templates & import wizard |
| EP-P1-11 | Audit ledger core | Append-only hash chain, anchoring, verifier |
| EP-P1-12 | PWA shell & device enrollment | Installable PWA, offline reference data, enrollment, design system, i18n |
| EP-P1-13 | Platform operator console v1 | Karry staff manage tenants, plans, health |

## 8.4 User stories & acceptance criteria

### EP-P1-01 Engineering platform

**US-P1-PLT-01 — Monorepo & module template** · *Persona: Engineer* · Must · M
As an engineer, I want a scaffolded monorepo with a module template, so that every new module follows the same architecture.
- AC1: `dotnet new karry-module -n Maintenance` creates Domain/Application/Infrastructure/Api/Contracts projects with references wired correctly.
- AC2: Architecture tests fail the build if a module references another module's Domain or Infrastructure.
- AC3: Frontend workspace (`apps/field`, `apps/portal`, `packages/*`) builds with one command; shared ESLint/TS config.
- AC4: `README` explains how to run everything locally in ≤ 10 commands.

**US-P1-PLT-02 — Local development environment** · Must · S
- AC1: `docker compose up` (or .NET Aspire) starts PostgreSQL (+PostGIS, TimescaleDB, pgvector), Keycloak (realm import), S3-compatible store with Object Lock (SeaweedFS, [ADR-0017](../../adr/0017-local-stack-and-object-storage.md)), Redis/Valkey, Mailpit.
- AC2: Seed script creates 2 demo tenants with users for each default role.
- AC3: A new engineer gets a running app in < 60 min (measured on onboarding).

**US-P1-PLT-03 — CI pipeline with quality gates** · Must · M
- AC1: On PR, run build, lint, format check, unit tests, architecture tests, integration tests, OpenAPI diff, CodeQL, Trivy, gitleaks.
- AC2: PR cannot merge if any gate fails or coverage on Domain projects drops below 80 %.
- AC3: Pipeline duration p50 < 12 min.

**US-P1-PLT-04 — CD to dev & staging with IaC** · Must · M
- AC1: OpenTofu provisions dev & staging (cluster/PaaS, managed PostgreSQL, object storage, Redis, KMS, DNS, TLS).
- AC2: Merge to `main` deploys to dev automatically; tagged RC deploys to staging.
- AC3: Database migrations run as a separate job before app rollout; failure aborts rollout.
- AC4: Images signed (cosign) with SBOM attached.

**US-P1-PLT-05 — Feature flags** · Must · S
- AC1: OpenFeature-compatible flags evaluable per tenant, user, environment.
- AC2: Flags can be changed without redeploy; changes are audited.

### EP-P1-02 Observability

**US-P1-OPS-01 — Distributed tracing & metrics** · Must · M
- AC1: Every API request produces a trace with `tenant_id`, `module`, `route`, `user_role` attributes; PII excluded.
- AC2: Grafana dashboards for RED metrics per module, DB pool stats, job queue lag.
- AC3: Correlation ID returned in `traceparent`/`X-Correlation-Id` and shown in UI error dialogs.

**US-P1-OPS-02 — Alerting & error tracking** · Must · S
- AC1: Alerts for: error rate > 2 % 5 min, p95 > 1 s 10 min, job lag > 5 min, DB CPU > 80 % 15 min, certificate expiry < 14 days.
- AC2: Sentry captures frontend errors with release version and tenant (hashed).
- AC3: On-call runbook exists for each alert.

### EP-P1-03 Identity & authentication

**US-P1-IAM-01 — Login with OIDC** · *Persona: any user* · Must · M
As a user, I want to sign in securely, so that only I can act under my name.
- AC1: Portal and PWA use Authorization Code + PKCE with Keycloak; tokens never stored in localStorage (memory + refresh via secure mechanism).
- AC2: Access token lifetime ≤ 10 min, refresh rotation enabled, refresh reuse detection revokes the session.
- AC3: Login page is localized FR/EN and shows tenant branding (logo, colour) when accessed through the tenant URL.
- AC4: Failed login lockout after configurable attempts; event audited.

**US-P1-IAM-02 — MFA for privileged roles** · Must · S
- AC1: Roles flagged `privileged` (Tenant Admin, Finance, Controller, Executive) must enrol TOTP or WebAuthn on first login.
- AC2: Users without MFA in a privileged role cannot access the portal (redirected to enrolment).

**US-P1-IAM-03 — User invitation & lifecycle** · *Persona: Tenant Admin* · Must · M
As a tenant admin, I want to invite users and deactivate leavers, so that access always matches my staff list.
- AC1: Invite by email or phone number (SMS/WhatsApp link) with role(s) and site scope; invitation expires in 7 days.
- AC2: Field users without email can be created with employee ID + PIN, enrolled via device QR (US-P1-OFF-02).
- AC3: Deactivation revokes sessions within 1 min and prevents device unlock; historical records keep the user's name.
- AC4: Office-seat count updates the usage meter (US-P1-BIL-04).

### EP-P1-04 Tenancy & isolation

**US-P1-TEN-01 — Tenant provisioning** · *Persona: Platform Operator* · Must · M
- AC1: Creating a tenant creates: tenant record, Keycloak organization/realm config, default roles, default settings (currency, locale, units, timezone), first admin invitation, subscription in `Trial` or chosen plan.
- AC2: Provisioning is idempotent and completes in < 30 s.
- AC3: Tenant has a unique slug; tenant URL `https://<slug>.karry.app` (or path-based fallback) resolves branding.

**US-P1-TEN-02 — Row-Level Security framework** · *Persona: Engineer* · Must · L→split
- AC1: Every tenant-scoped table has `tenant_id uuid not null`, RLS enabled + forced, policy `USING/WITH CHECK` on `app.tenant_id`.
- AC2: Request pipeline sets the tenant transaction-locally (`set_config('app.tenant_id', $1, true)` ≡ `SET LOCAL`, batched with the first statement per spike WP-P0-12) from the validated token inside the request transaction; missing tenant → request rejected.
- AC3: App DB role has no `BYPASSRLS` and is not the table owner.
- AC4: EF Core global query filter on `TenantId` as second layer.
- AC5: A migration linter fails CI if a new table lacks `tenant_id` + RLS (unless whitelisted as global reference data).

**US-P1-TEN-03 — Automated tenant-isolation test suite** · Must · M
- AC1: For every endpoint in OpenAPI, the suite creates data in tenant A and calls the endpoint with a tenant B token; expects 403/404 and no data in body.
- AC2: Runs on every PR and nightly; a failure blocks merge.

**US-P1-TEN-04 — Tenant settings** · *Persona: Tenant Admin* · Must · S
- AC1: Admin can set legal name, logo, primary colour, default language, functional currency, unit system (metric/imperial), default timezone, fiscal year start.
- AC2: Changes are audited and applied without re-login.

### EP-P1-05 Plans & entitlements

**US-P1-BIL-01 — Plan catalogue** · *Persona: Platform Operator* · Must · M
As the Karry business, I want plans defined as data, so that we can sell tiers and change packaging without deployments.
- AC1: Plan = code, version, features list, limits (sites, assets/site, office users, AI tokens, storage GB, edge agents), price book per currency.
- AC2: Plans are versioned; existing subscriptions stay on their version until migrated.
- AC3: Starter, Professional, Business, Enterprise seeded per [§2.3](../../01-product/business-model-and-pricing.md#23-subscription-plans-illustrative-validated-in-phase-0-with-real-prospects).

**US-P1-BIL-02 — Subscription management (manual billing)** · Must · M
- AC1: Platform operator assigns plan + add-ons + billing period (monthly/annual) + quantities to a tenant.
- AC2: Subscription state machine per [§2.5](../../01-product/business-model-and-pricing.md#25-tenant-subscription-lifecycle) implemented (Trial, Active, PastDue, Restricted, Suspended, Cancelled, Expired, Deleted); transitions audited.
- AC3: Manual invoice records (number, amount, currency, due date, paid date) can be entered until billing provider integration in P10.

**US-P1-BIL-03 — Feature gating** · Must · M
- AC1: API attribute/filter `RequiresFeature("<code>")` returns problem `feature_not_in_plan` (HTTP 402 or 403 per ADR) when not entitled.
- AC2: Frontend `useEntitlement('<code>')` hook hides or locks features with an upgrade card (value message + "Contact sales / Upgrade").
- AC3: Entitlement resolution cached per tenant ≤ 60 s; plan change propagates within 60 s.

**US-P1-BIL-04 — Usage meters & soft limits** · Must · M
- AC1: Daily snapshot of sites, assets per site, active office users, storage; stored per tenant per day.
- AC2: When usage exceeds a limit, the admin sees a banner and Karry ops get a notification. Within the 10 % grace margin nothing is blocked; beyond it, only *creation* of new over-limit items is blocked, never field capture.
- AC3: Usage visible to tenant admin in "Plan & usage" page.

**US-P1-BIL-05 — Subscription state enforcement** · Must · S
- AC1: Restricted state: office app read-only (except billing page) while field capture continues.
- AC2: Suspended: everything read-only; export allowed.
- AC3: Cancelled/Expired: login shows export & reactivate options only.

### EP-P1-06 Authorization

**US-P1-IAM-04 — Default roles** · Must · S
- AC1: Seed roles per [Appendix A – Default Role & Permission Matrix](../../03-domain/catalogues/roles-and-permissions.md) with their permissions.
- AC2: Default roles cannot be deleted, but can be cloned.

**US-P1-IAM-05 — Custom roles & permissions editor** · *Persona: Tenant Admin* · Must · M
As a tenant admin, I want to define who can see and do what, so that field staff only see what they need.
- AC1: Admin creates a role from scratch or by cloning; selects permissions grouped by module (resource × action).
- AC2: Permission matrix UI shows effective permissions; changes audited with before/after.
- AC3: Privileged permission changes require the admin to re-authenticate (step-up).

**US-P1-IAM-06 — Scope assignment (ABAC)** · Must · M
- AC1: Users are assigned roles **per scope**: tenant-wide, specific site(s), specific warehouse(s).
- AC2: All list endpoints filter by the user's scopes; direct access to out-of-scope IDs returns 404.

**US-P1-IAM-07 — Field-level masking** · Must · M
- AC1: Admin can set READ / WRITE / MASKED / HIDDEN for configurable sensitive fields (e.g., unit prices, cost rates, margins, operator wages, loan amounts).
- AC2: Masking is applied in API serialisation, exports, PDFs and (later) AI tools; the masked value shows `•••` with a tooltip "hidden by policy".
- AC3: Automated test proves a masked field never appears in any JSON response for that role.

### EP-P1-07 Organization & sites

**US-P1-ORG-01 — Manage sites** · *Persona: Tenant Admin* · Must · M
- AC1: Create/edit/archive site: name, code, type (quarry, sand washing, mine, warehouse-only, workshop), address, GPS location (map pin), boundary polygon (optional, drawn on map), timezone, currency override.
- AC2: Site count updates usage meter; creation beyond plan limit follows soft-limit rules.

**US-P1-ORG-02 — Shift patterns & calendars** · Must · M
- AC1: Define shift patterns per site (e.g., Day 06:00–18:00, Night 18:00–06:00; or 3×8), effective from a date.
- AC2: Define non-working days and public holidays per site (used later by weather engine).
- AC3: Shift instances are generated for the next 30 days and are computable for any date.

### EP-P1-08 Assets & meters

**US-P1-AST-01 — Asset types & classifications** · Must · S
- AC1: Seeded types: excavator, wheel loader, haul truck, dozer, grader, drill rig, crusher (jaw, cone, impact, VSI), screen, washer, conveyor, generator, fuel bowser, light vehicle, other.
- AC2: Each type declares default meter(s) (HOURS, KM, none) and default component templates (used in P4).

**US-P1-AST-02 — Asset registry** · *Persona: Site Manager* · Must · M
As a site manager, I want to register my machines, so that all operations can be attributed to them.
- AC1: Fields: internal code, name, type, make, model, serial/VIN, registration plate, year, acquisition date, current site/location, status (active, in repair, idle, disposed), photo, QR label.
- AC2: Printable QR label per asset (PDF, A4 sheet of labels) that the PWA can scan to select the asset.
- AC3: Asset count per site updates usage meter.
- AC4: Search by code, plate, make/model; filter by site, type, status.

**US-P1-AST-03 — Meters** · Must · M
- AC1: Each asset has 0..n meters (HOURS, KM) with initial reading, installation date, status (OK, FAILED, REPLACED).
- AC2: Meter history is append-only; meter replacement (P2) creates a new meter with offset.

**US-P1-AST-04 — Materials & products** · Must · M
- AC1: Define materials (raw rock types, products/fractions, e.g., "Gravel 8/16") with: bank density, loose density, compacted density, swell factor, default moisture %, effective date.
- AC2: Density changes are versioned; historical conversions use the version valid at event time.

### EP-P1-09 Units engine

**US-P1-UNI-01 — Quantity value object and conversion service** · Must · M
- AC1: `Quantity(value, unit, state?)` supports M3 (with state), T, ST, KG, L, KM, H, DAY, PCS; arithmetic only between compatible units.
- AC2: Conversion volume↔mass uses material density of matching state and moisture ([Computational Engines (Corrected Mathematics) §10.1](../../03-domain/computational-engines.md#101-units--conversion-engine)).
- AC3: Property tests: round-trip `t → m³ → t` within 1e-9 relative; conversions monotonic; converting between states via mass is consistent with the swell factor.
- AC4: API serializes quantities as `{value, unit, state}`; frontend `<QuantityInput>` component supports toggle and shows factors used on tap.

### EP-P1-10 Data import

**US-P1-ORG-03 — Excel import wizard** · *Persona: Tenant Admin / Karry onboarding specialist* · Must · L→split
As a new customer, I want to import my machines, materials, users and sites from Excel, so that I can start using Karry the same day.
- AC1: Downloadable templates (FR/EN) for sites, assets, meters (initial readings), materials, users.
- AC2: Upload → validation report per row (errors and warnings, with cell references) → preview → commit.
- AC3: Import is transactional per file (all or nothing) and idempotent on natural keys (asset code, user employee ID).
- AC4: 1,000 assets import in < 60 s.
- AC5: Import job audited; original file stored for 90 days.

### EP-P1-11 Audit ledger core

**US-P1-AUD-01 — Append-only hash-chained ledger** · *Persona: Auditor (system)* · Must · L→split
- AC1: `audit.ledger_entry` per [Trust Layer: Verification, Signatures & Audit Ledger §13.3](../../02-architecture/trust-layer-signatures-audit.md#133-hash-chained-ledger); entries written in the same transaction as the business change via the domain event pipeline.
- AC2: DB trigger rejects UPDATE/DELETE/TRUNCATE; app role has INSERT/SELECT only.
- AC3: Sequence is gap-free per tenant under concurrency (test with 50 parallel writers).
- AC4: Payload canonicalized with RFC 8785 before hashing.

**US-P1-AUD-02 — Anchoring to WORM storage** · Must · M
- AC1: Hourly job writes chain heads `{tenant, seq, hash, timestamp}` to object storage with Object Lock (compliance mode, retention per plan).
- AC2: Anchor job failure raises an alert; next run catches up.

**US-P1-AUD-03 — Ledger verifier** · *Persona: Tenant Admin / Auditor* · Must · M
- AC1: CLI `karry-ledger verify --tenant <id>` recomputes the chain and compares with anchors; outputs OK or first broken sequence.
- AC2: Admin UI "Integrity check" runs verification and shows last result and date.
- AC3: Test: manually altering a row in a test DB is detected.

**US-P1-AUD-04 — Audit log viewer** · *Persona: Tenant Admin* · Must · M
- AC1: Filter by actor, entity type, entity ID, date range, event type; shows before/after diff.
- AC2: Export CSV (respecting masking).

### EP-P1-12 PWA shell & device enrollment

**US-P1-OFF-01 — Installable offline app shell** · *Persona: Field Operator* · Must · M
- AC1: Field app installable ("Add to Home screen"); starts offline after first load in < 3 s on reference device.
- AC2: Service worker update prompts only on the home screen, never during a form.
- AC3: Storage persistence requested; app shows a warning if denied.

**US-P1-OFF-02 — Device enrollment** · *Persona: Tenant Admin / Site Manager* · Must · M
As a site manager, I want to enrol site tablets with a QR code, so that only trusted devices can submit field data.
- AC1: Admin generates an enrollment QR (valid 15 min) for a site; tablet scans it → generates non-extractable ECDSA key pair → registers public key + device info → device bound to tenant/site.
- AC2: Device list shows name, model, app version, last sync, status; admin can revoke.
- AC3: Revoked device cannot sync; its unsynced data, if later uploaded, is quarantined for review.

**US-P1-OFF-03 — Offline reference data** · Must · M
- AC1: After login on an enrolled device, reference data for the user's scopes (sites, assets, meters, materials, shift patterns, users of the site) is downloaded to IndexedDB.
- AC2: Delta pull keeps it fresh (`/sync/v1/changes`); last refresh time visible.
- AC3: 2,000 assets + 500 users load offline lists in < 500 ms on reference device.

**US-P1-OFF-04 — Shared tablet quick user switch** · Must · M
- AC1: Users of the site can unlock the shared tablet with employee ID + PIN (offline, validated against locally cached salted hash).
- AC2: Offline session max 72 h (configurable) before online re-authentication is required.
- AC3: Auto-lock after inactivity (configurable, default 5 min).

**US-P1-OFF-05 — Design system & outdoor mode** · Must · M
- AC1: Component library (buttons, inputs, quantity input, selectors, lists, dialogs, toasts, status chips) in Storybook with light, dark and outdoor high-contrast themes.
- AC2: Touch targets ≥ 48 px; minimum body font 16 px in field app.
- AC3: axe-core zero serious violations in Storybook.

**US-P1-OFF-06 — Internationalization FR/EN** · Must · S
- AC1: Language switch instantly in both apps; default from tenant then user preference.
- AC2: Pseudo-localization build available in staging.

### EP-P1-13 Platform operator console

**US-P1-OPS-03 — Tenant management console** · *Persona: Platform Operator* · Must · M
- AC1: List tenants with plan, state, sites, assets, last activity, MRR.
- AC2: Create tenant, change plan/state, record invoices.
- AC3: Support access ("impersonation") requires tenant admin consent token or break-glass with reason, is time-boxed (1 h) and fully audited in the tenant's ledger.

## 8.5 Technical scope

**Backend**
- `Karry.SharedKernel`: typed IDs (UUIDv7), `Money`, `Quantity`, `Result<T>`, domain event base, clock abstraction.
- `Karry.BuildingBlocks`: tenancy middleware, RLS session setter, outbox + dispatcher, idempotency store, problem details, authorization policies (permission + scope + field masking serializer), entitlement filter, OpenTelemetry setup.
- Modules: `Tenancy`, `Billing` (plans/subscriptions/usage), `Identity` (users, roles, devices), `Organization`, `Assets`, `Trust` (ledger only in P1).
- Worker host: outbox dispatcher, anchoring job, usage snapshot job, shift instance generator.

**Frontend**
- `apps/portal`: admin screens (tenant settings, users, roles, sites, assets, materials, import, audit viewer, plan & usage, devices).
- `apps/field`: shell, login/unlock, enrollment, reference data browser (assets list + QR scan), settings, sync status.
- `packages/ui`, `packages/i18n`, `packages/api-client` (generated), `packages/offline` (Dexie schema v1, sync client pull), `packages/crypto` (device key).

**Data (schemas created)**
`tenancy`, `billing`, `identity`, `organization`, `assets`, `audit`, `platform`.

**Key APIs**

| Method | Route | Purpose |
|---|---|---|
| POST | `/api/v1/platform/tenants` | Provision tenant (operator) |
| GET/PUT | `/api/v1/tenancy/settings` | Tenant settings |
| GET | `/api/v1/billing/entitlements` | Resolved entitlements for current tenant |
| GET | `/api/v1/billing/usage` | Usage vs limits |
| CRUD | `/api/v1/identity/users`, `/roles`, `/role-assignments` | Users & roles |
| GET/PUT | `/api/v1/identity/field-policies` | Field masking rules |
| POST | `/api/v1/identity/devices/enrollments` | Create enrollment token |
| POST | `/api/v1/identity/devices` | Register device (from tablet) |
| CRUD | `/api/v1/organization/sites`, `/shift-patterns`, `/calendars` | Org |
| CRUD | `/api/v1/assets`, `/assets/{id}/meters`, `/materials` | Assets & materials |
| POST | `/api/v1/imports` (+ `/validate`, `/commit`) | Excel import |
| GET | `/api/v1/audit/entries`, POST `/api/v1/audit/verify` | Ledger |
| GET | `/sync/v1/changes` | Reference data delta pull |

**Infrastructure**
- dev + staging environments, KMS keys (ledger anchoring signing, later envelope countersigning), object storage buckets (`photos`, `documents`, `anchors` with Object Lock), Keycloak deployment with DB, backups enabled with PITR.

## 8.6 Test plan (Phase 1)

**Scope:** all P1 stories + platform NFRs.

| Test ID | Type | Description | Expected |
|---|---|---|---|
| TC-P1-TEN-001 | Integration | Create data in tenant A, query with tenant B context at SQL level | 0 rows |
| TC-P1-TEN-002 | Isolation suite | All endpoints, cross-tenant token | 403/404, empty body |
| TC-P1-TEN-003 | Integration | Request without tenant claim | 401/400, no DB query executed |
| TC-P1-TEN-004 | Migration lint | Add table without `tenant_id` | CI fails |
| TC-P1-TEN-005 | Load | 10k mixed-tenant requests | 0 leaks, p95 < 300 ms |
| TC-P1-IAM-001 | E2E | Login FR/EN, branding by tenant URL | Branding & language correct |
| TC-P1-IAM-002 | E2E | Privileged user without MFA | Forced enrolment |
| TC-P1-IAM-003 | Integration | Refresh token reuse | Session revoked |
| TC-P1-IAM-004 | E2E | Deactivate user → tablet unlock attempt after sync | Denied |
| TC-P1-IAM-005 | API | Masked field for role X across all endpoints & exports | Never present |
| TC-P1-IAM-006 | API | Out-of-scope site asset by ID | 404 |
| TC-P1-BIL-001 | Unit | Entitlement resolution: plan + add-ons + overrides | Correct feature set |
| TC-P1-BIL-002 | API | Call gated endpoint without feature | `feature_not_in_plan` problem |
| TC-P1-BIL-003 | E2E | Locked feature shows upgrade card | Card visible, no data leaked |
| TC-P1-BIL-004 | Integration | Exceed asset limit by 5 % / 15 % | Banner only / creation blocked, field unaffected |
| TC-P1-BIL-005 | Integration | Subscription → Restricted | Office read-only, field sync accepted |
| TC-P1-UNI-001 | Property | Round-trip conversions (10k random cases) | Within tolerance |
| TC-P1-UNI-002 | Unit | Density version at historical date | Correct version used |
| TC-P1-UNI-003 | Unit | Adding m³ BANK to m³ LOOSE without conversion | Rejected |
| TC-P1-AUD-001 | Integration | 50 parallel writers | Gap-free sequence, valid chain |
| TC-P1-AUD-002 | Integration | Direct UPDATE on ledger as app role | Permission denied |
| TC-P1-AUD-003 | Integration | Tamper row as superuser in test DB → verify | Detected at exact seq |
| TC-P1-AUD-004 | Integration | Anchor job outage 3 h → recovery | All heads anchored |
| TC-P1-ORG-001 | E2E | Import 1,000 assets with 20 erroneous rows | Validation report lists 20 rows with cell refs; commit blocked until fixed |
| TC-P1-ORG-002 | Integration | Re-import same file | No duplicates |
| TC-P1-OFF-001 | Device | Install PWA, airplane mode, cold start | < 3 s, shell works |
| TC-P1-OFF-002 | Device | Enrollment QR expired | Rejected with clear message |
| TC-P1-OFF-003 | Device | Offline unlock with PIN, 73 h after last online | Requires online login |
| TC-P1-OFF-004 | Device | Reference data 2k assets offline search | < 500 ms |
| TC-P1-OFF-005 | Device | Revoke device → sync | Rejected; quarantine flag |
| TC-P1-PLT-001 | Pipeline | Introduce cross-module reference | Build fails |
| TC-P1-PLT-002 | Pipeline | Introduce secret in code | gitleaks blocks |
| TC-P1-OPS-001 | Ops | Kill API pod under load | No failed requests beyond retry budget |
| TC-P1-OPS-002 | Ops | Restore staging DB from PITR to T-1h | Success, documented duration |
| TC-P1-PLT-003 | Integration | `dotnet new karry-module` output (P1.1) | Builds; 5 projects; sample test passes |
| TC-P1-PLT-004 | Smoke | `docker compose up` (P1.1) | All services healthy; 4 extensions created; S3 Object Lock (compliance) blocks delete/overwrite |
| TC-P1-PLT-005 | Pipeline | Coverage below 80 % on Domain / format violation (P1.2) | CI fails |
| TC-P1-PLT-006 | Property | `Money` arithmetic and rounding (P1.3) | No currency mixing; decimal only |
| TC-P1-PLT-007 | Integration | Outbox (P1.3) | Change + events commit/roll back together; each message delivered once; failure rolls back all subscriber effects and retries with back-off; dead-letter after max attempts or unknown type; a message held by another worker is skipped (SKIP LOCKED), never waited for; later events of an aggregate wait while an earlier one is in flight; 4 concurrent workers: exactly once and per-aggregate order |
| TC-P1-PLT-008 | API | Idempotency-Key replay (P1.3) | Original response; no second execution |
| TC-P1-PLT-009 | Integration | Feature flag change (P1.10) | Effective without redeploy; ledgered |
| TC-P1-PLT-010 | Pipeline | Failing migration during deploy (P1.17) | Rollout aborted; version unchanged |
| TC-P1-PLT-011 | Ops | Rollback to previous release (P1.17) | Works; duration recorded |
| TC-P1-OPS-003 | Integration | Trace attributes on API request (P1.4) | 4 attributes present; no PII |
| TC-P1-OPS-004 | API | Support access consent / break-glass (P1.16) | Denied without; expires 1 h; ledgered |
| TC-P1-TEN-006 | Integration | Provision same tenant twice (P1.5) | One tenant; < 30 s |
| TC-P1-TEN-007 | Integration | Tenant settings change (P1.11) | Ledgered; applied without re-login |
| TC-P1-AUD-005 | API | Audit CSV export (P1.16) | Masking and scope respected |
| TC-P1-AUD-006 | Integration | Business change rolled back (P1.6) | No ledger entry, no outbox row |
| TC-P1-IAM-007 | Integration | Deactivate user (P1.8) | API access lost ≤ 60 s; history keeps name |
| TC-P1-IAM-008 | Integration | Invitation expiry / reuse (P1.8) | Rejected |
| TC-P1-IAM-009 | API | Delete default role; privileged change without step-up (P1.9) | Rejected |
| TC-P1-BIL-006 | Unit | Subscription state machine; plan versioning (P1.10) | Illegal transitions rejected; version kept |
| TC-P1-ORG-003 | Property | Shift instances over DST / midnight / holidays (P1.11) | Correct instances |
| TC-P1-ORG-004 | Performance | Import 1,000 assets (P1.14) | < 60 s |
| TC-P1-AST-001 | API | QR label scanned in another tenant (P1.13) | Not found |
| TC-P1-AST-002 | Integration | Update/delete meter history (P1.13) | Denied; readings ledgered |
| TC-P1-OFF-006 | Accessibility | axe over all Storybook stories and themes (P1.7) | 0 serious/critical |
| TC-P1-OFF-007 | Unit/E2E | i18n keys and pseudo-localization (P1.7) | No missing keys; no hard-coded text |
| TC-P1-OFF-008 | E2E | Non-extractable key; re-enrollment after clearing site data (P1.15) | Old key rejected; new enrollment works |
| TC-P1-PLT-012 | Integration | Health endpoints (P1.1) | `/health/live` 200 without DB; `/health/ready` 503 when DB missing/unreachable (body reveals nothing), 200 when DB answers |
| TC-P1-PLT-013 | Unit | Module registration (P1.1) | Services of every module registered; duplicate module names rejected; endpoints mapped under `/api/v1/{module}` only |
| TC-P1-PLT-014 | Architecture | Layer and dependency rules (P1.1) | Upward layer references, infrastructure packages in Domain/Contracts, references to hosts, kernel depending upwards, projects missing from the solution, bad module project names: each fails the build |
| TC-P1-PLT-015 | Unit (Vitest) | Portal and field app shells (P1.1) | Each app renders its product name as the level-1 heading inside the `main` landmark; workspace lint (type-aware), typecheck and build pass |
| TC-P1-PLT-016 | Unit | Merge guard (P1.2) | Refuses missing/failed/cancelled required checks, any other failing check, sensitive paths without owner approval; waits on pending checks; fails closed on errors |
| TC-P1-PLT-017 | Unit | Typed entity IDs (P1.3) | UUIDv7, time-ordered with the clock, unique within a millisecond; empty and non-v7 GUIDs rejected |
| TC-P1-PLT-018 | Unit | Result, Error and aggregate events (P1.3) | Failures short-circuit Map/Bind; value of a failure throws with its code; events collected in order with clock time |
| TC-P1-PLT-019 | Integration | Problem details (P1.3) | Each error kind → its status (400/403/404/409/422) with RFC 9457 body, stable `code`, `type` URI and `traceId`; framework errors use the same shape |
| TC-P1-PLT-020 | Integration | Request validation (P1.3) | Invalid body → 400 `request.invalid` with per-field errors before the handler runs; valid body reaches the handler |
| TC-P1-PLT-021 | Integration | Unhandled exceptions (P1.3) | Generic 500 `server.unexpected_error` with trace ID; no exception type, message, host, user or stack trace in the body |
| TC-P1-PLT-022 | Integration | OpenAPI description (P1.3) | `/openapi/v1.json` (OpenAPI 3.x) outside production; 404 in production |

**Non-functional tests:** load test baseline (k6) for auth + reference data pull: 500 concurrent devices pulling deltas; DB failover test on managed PostgreSQL; backup restore drill #1.

**Security tests:** ZAP baseline scan on portal & API; review of Keycloak configuration (password policy, token lifetimes, brute-force protection); threat model items for tenancy validated.

**UAT:** pilot customer admin imports their real asset list and creates users (assisted session).

## 8.7 Deliverables

- Running dev & staging environments, IaC, CI/CD, observability.
- Portal admin features, field app shell with enrollment & offline reference data.
- Plans & entitlements live (manual billing).
- Ledger core with verifier.
- Runbooks: deploy, rollback, restore, on-call alerts, tenant provisioning.
- ADRs updated; OpenAPI v1 published internally.

## 8.8 Demo script

1. Platform operator provisions "Carrière Pilote SA" on Professional trial.
2. Tenant admin logs in (FR), enrols MFA, uploads logo, imports 60 machines from Excel (shows validation errors, fixes, commits).
3. Admin creates a custom role "Chef de poste" with cost fields masked; shows API response masked.
4. Admin generates enrollment QR; tablet enrols; airplane mode; operator unlocks with PIN; searches asset by QR scan offline.
5. Audit viewer shows every action; run "Integrity check" → OK. Tamper demo on a throwaway DB → detected.
6. Plan & usage page; switch plan to Starter → Maintenance menu shows upgrade card.

## 8.9 Exit gate (M1 — Walking Skeleton)

- [ ] All Must stories done; isolation suite green; ledger verifier green.
- [ ] Staging deploy from merge in < 30 min; rollback tested.
- [ ] Restore drill executed with RTO/RPO documented.
- [ ] Pilot customer's master data loaded in staging.
- [ ] No Sev-1/Sev-2 open.

## 8.10 Risks

| Risk | Mitigation |
|---|---|
| Keycloak complexity slows the team | Use realm export as code, limit customisation to themes, timebox |
| RLS mistakes | Migration linter + isolation suite + code review checklist |
| Scope creep in admin UI | Admin screens use standard list/detail generator patterns |

## 8.11 Commercial outcome

Plans and entitlements exist. The pilot customer's data is loaded. Sales can show a working admin portal and the trust story (ledger verification demo).

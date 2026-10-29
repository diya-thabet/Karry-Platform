---
title: "ADR-0029: Operator console, consented support access and the tenant audit viewer"
area: adr
tags: [operator, console, support-access, break-glass, impersonation, audit, ledger, csv, masking, integrity, mfa]
source: "Phase 1 sub-phase P1.16"
status: proposed
last_updated: 2026-10-28
---

# ADR-0029 — Operator console, consented support access and the tenant audit viewer

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (P1.16 slice 1, 2026-10-28)
- **Date:** 2026-10-28
- **Deciders:** Product owner, software architect
- **Related:** US-P1-OPS-03 (AC1–AC3), US-P1-AUD-04 (AC1–AC2), US-P1-AUD-03 AC2; TC-P1-OPS-004, TC-P1-AUD-005;
  threat T-14 ([threat model](../02-architecture/threat-model.md)); [ADR-0008](0008-hash-chained-ledger.md) (ledger),
  [ADR-0012](0012-identity-authorization.md) (identity), [ADR-0022](0022-authorization-engine.md) (permissions and field masking),
  [ADR-0023](0023-plans-subscriptions-entitlements.md) (plans and subscriptions)

## Context

Karry's own staff (platform operators) run every customer's subscription, and sometimes need to look at a customer's
data to help them. Each customer's admin must be able to see who did what in their company, export it, and check
that nothing was altered. The questions:

1. **What a ledger entry says about who and what.** Every ledgered event carries its own fields (`UserId`,
   `AssetId`, a role code…) and most carry an `Actor` and `Before`/`After`; the viewer filters by actor, entity type
   and entity ID (AUD-04 AC1) over years of entries.
2. **How the export respects masking** (AUD-04 AC2) without a second masking implementation.
3. **How the operator console reads every tenant** without weakening row-level security.
4. **How support staff see a customer's data** (OPS-03 AC3, T-14): with the customer's consent, or break-glass
   with a reason and an alert; for one hour; all of it in the customer's own ledger.
5. **How operators are protected** themselves: their account opens every customer's billing.

## Decision

### 1. Every ledgered event names its subject; an index is kept beside the chain

- Each event type with `[EventType]` also has **`[AuditSubject(entity, idProperty)]`**, for example
  `[AuditSubject("user", nameof(UserId))]` or `[AuditSubject("role", nameof(Role))]`. An architecture test refuses
  an event type without it. The actor is the event's `Actor` property when it has one (the caller's subject, or a
  system name such as `seed-demo`), else none.
- The ledger writer stores **`audit.ledger_subjects (tenant_id, seq, actor, entity_type, entity_id, site_id)`** in the same
  transaction as the entry: append-only (trigger plus revoked `UPDATE`/`DELETE`/`TRUNCATE`), row-level security,
  indexed for each filter. It is **derived from the hashed payload, never hashed itself**: a tampered index row can
  mislead a filter but never the entry, whose payload the viewer always shows and the integrity check verifies.
- `Karry.Host migrate` **fills missing index rows** from the stored payloads after the migrations (idempotent), so
  existing ledgers become searchable and a changed mapping can be applied again.

- **Site of an entry (added while building):** the roles matrix gives Controller and Site Manager the audit log **on
  their sites** only. The index therefore also stores `site_id`: the event's site property (`Site = nameof(ToSiteId)`,
  else `SiteId`, else the ID of a `site` entity), otherwise the **entity's latest known site** in the index (an
  asset's status change belongs to the asset's site). A reader holding `audit.ledger.read` on some sites sees only
  entries of those sites; entries without a site (company, users, roles, materials, subscriptions) only company-wide
  readers see. Fail-closed: a site-scoped reader may miss an entry, never see another site's.
- **As built (2026-10-28, US-P1-AUD-04 slice 1):** `AuditSubjectAttribute` / `AuditSubject.Of` in
  `Karry.SharedKernel.Domain` (entity names `^[a-z][a-z_]*$`, `Guid` IDs stored in `D` form); 61 event types
  annotated; `LedgerRecord.Subject`; migration `LedgerSubjects` (foreign key to the entry, so the owner's plain
  `TRUNCATE` of the entries now fails on the key before the trigger); `ILedgerSubjectBackfill` run by
  `Karry.Host migrate` ("Audit index: N ledger entries given their subject"). The architecture rule also refuses an ID
  property that is neither `Guid` nor `string` (it would be stored as `ThingId { Value = … }`). Details:
  [trust layer §13.3.1](../02-architecture/trust-layer-signatures-audit.md#1331-as-implemented-p16-trust-module-schema-audit).

### 2. The audit viewer API (Trust module, routes under `/api/v1/audit`)

- `GET /api/v1/audit/entries?actor=&entityType=&entityId=&siteId=&eventType=&from=&to=&cursor=&limit=` (newest
  first, 50 a page by default, at most 200, keyset cursor on the sequence), permission **`audit.ledger.read`**: the
  roles of the matrix row "Audit log / integrity check" (Tenant Admin, Executive, Finance company-wide; Controller and
  Site Manager on their sites). A holder on some sites sees only those sites' entries (§1); asking for another site
  does not widen it. Readable whatever the subscription state, like exports (ADR-0023).
- Each entry is turned back into **its typed event** (the event type registry the outbox already uses) and written
  through the normal API JSON contract, so **`[Sensitive]` fields are masked exactly as everywhere else**. An entry
  whose type is no longer known is shown with its event type and no event.
- **Before/after diff:** for events with `before` and `after`, the server lists the changed values (`path`,
  `before`, `after`; objects field by field, lists as a whole), computed on the event **after masking**: a masked
  field is absent on both sides and **never listed**, so the caller cannot even learn that it changed.
- **CSV export** `GET /api/v1/audit/entries/export.csv` with the same filters, reach and masking, streamed, at most
  100 000 rows (a larger range is refused with `audit.export_too_large`), UTF-8 with BOM for Excel, formula
  injection neutralised. Each export is itself ledgered (`trust.audit_exported.v1` with the filters and row count;
  event names take their module's assembly name, so `trust.`, although the routes are under `/audit`).
- **CSV format (decided while building):** `;` between cells (the list separator of French Excel, which most users
  have: a comma file opens as one column there), UTF-8 with a byte order mark, CRLF lines, RFC 4180 quoting (cells
  containing `;`, `,`, quotes or line breaks are quoted). Columns, stable English codes for auditors' tools:
  `sequence;occurred_at;event_type;actor;actor_name;entity_type;entity_id;site_id;changes;event`. `occurred_at` in UTC with
  microseconds; `changes` one `path: before → after` per line (JSON values); `event` the masked event as JSON. A
  cell starting with `= + - @`, a tab or a carriage return gets a leading apostrophe.
- **Integrity check** `POST /api/v1/audit/verify` (exists, AUD-03 AC1) now also **records the result**
  (`audit.verifications`, row-level security) and `GET /api/v1/audit/verifications/latest` returns the last one for
  the "Integrity check" screen (AUD-03 AC2). Permission `audit.ledger.verify` (unchanged: the same roles as the log).
- **As built (2026-10-29, slice 3b, portal):** the portal's **Audit** section (`/audit`, `audit/AuditPage.tsx`) shows
  the **Integrity check** card to holders of `audit.ledger.verify` (last result, entries and sealed copies checked,
  first broken entry, when and by whom; "Check now") and the **audit log** to holders of `audit.ledger.read`: filters
  person (from `/actors`), site, event code, kind of record, record ID and period (whole days where the person is),
  applied together; 50 entries a page with "Show more"; an entry's dialog lists its before/after changes, the fields
  the policy hides (lock, never their value) and the event as recorded; "Export to Excel (CSV)" downloads the
  filtered entries and says the export is recorded. Someone with neither permission sees a notice, and nothing is
  asked of the API. Event and record codes are shown as recorded (stable English codes, not translated).
- **People's names (decided while building, slice 3a):** the trail stores the actor's Keycloak subject (an
  opaque ID), which an auditor cannot read. Trust asks Identity through its contract **`IUserNames`**
  (`Identity.Contracts`, subject → "First Last", row-level security keeps it inside the company) and adds
  **`actorName`** to each entry and an **`actor_name`** CSV column (empty for a system actor or a person removed
  from the company; the subject stays the reference). **`GET /api/v1/audit/actors`** (`audit.ledger.read`) lists the
  people present in the trail within the caller's reach (at most 500, by subject) with their names, for the
  viewer's "person" filter; the latest integrity check names its verifier the same way (`verifiedByName`). Names are not ledgered: they are resolved when read, so a renamed person shows their
  current name (the event payload is unchanged evidence).
- **As built (2026-10-29, slice 2c, export):** `AuditExports` checks the filters like the viewer, counts the matching
  entries (refused above 100 000), records an `AuditExport` aggregate (`audit.exports`, RLS) whose
  `trust.audit_exported.v1` event is ledgered in the same transaction **before any row is sent**, then streams the
  entries up to the newest one counted (so the file holds exactly the recorded rows and never its own entry). Each row
  goes through the same `AuditEntryResponse` as the JSON API (masking, changes). Trust's outbox is now dispatched by
  `karry-workers`.
- **As built (2026-10-29, slice 2b):** `IntegrityChecks` runs the existing `VerifyLedgerHandler` (unchanged, also used
  by the CLI) and keeps the result through `IVerificationRecords` in `audit.verifications` (append-only like the
  ledger: a past result is evidence of what was found). A check is not ledgered: it changes no business state.
  `GET /api/v1/audit/verifications/latest` answers `{verification, verifiedBy}`, both null before the first check (`verifiedByName` added in slice 3a).
- **As built (2026-10-29, slice 2a):** `AuditLogQueries` + `IAuditLog` (`EfAuditLog`: entries left-joined to the
  subject index, so an unknown event type still shows to company-wide readers when unfiltered), `AuditChanges`,
  `AuditEntryResponse` (typed event through the host's JSON options, `eventId`/`occurredAt` only on the entry). Errors:
  `audit.limit_invalid`, `audit.cursor_invalid`, `audit.range_invalid`, `audit.filter_invalid` (a filter over 200
  characters).

### 3. The operator console reads tenants one at a time, through each module

- A BuildingBlocks contract **`ITenantSummarySource`** (like `ISyncSource`): each module that owns a column of the
  list answers for the current tenant (Organization: sites; Assets: machines; Billing: plan, state, MRR; Trust: last
  activity = time of the last ledger entry). The Tenancy module lists tenants from the global directory, then opens
  **one scope per tenant with `ITenantContext` set**, as the anchoring job does: **row-level security is never
  bypassed** and no module reads another's tables.
- `GET /api/v1/tenancy/tenants` (platform operators only). Fine up to about a thousand tenants; past that the
  summaries move to a nightly platform read model (recorded in Consequences).
- Create tenant, change plan or state, record invoices use the **existing operator endpoints** (P1.4, P1.10).
- **MRR** is the monthly price of the subscription's plan version plus add-ons, in the plan's currency, zero during a
  trial or when suspended or ended; illustrative until the owner sets prices (N-01).
- **As built (2026-10-29, slice 4a):** `ITenantSummarySource` (BuildingBlocks.Tenancy) returns a
  `TenantSummaryPart` (plan code and version, subscription state, trial end, MRR and its currency, last activity);
  parts are merged column by column. The counts reuse the existing **`IUsageSource`** meters (sites, machines, office
  users), so Organization, Assets and Identity needed nothing new. Sources: Billing `BillingSummary`, Trust
  `LedgerActivity` (newest ledger entry). `TenantOverview` (Tenancy.Application) lists tenants by name and opens one
  scope per tenant with `ITenantContext` set; a tenant whose module fails is listed with `complete: false` (error log
  6001) and never hides the others. **MRR decided while building** (`MonthlyRevenue`, Billing.Domain): the plan
  version's price for the subscription's period in the **company's currency when the plan prices it, else the plan's
  first currency**; sites × per-site price + extra machines × extra-asset price; an annual price ÷ 12, rounded to the
  currency's minor units; billed states are active, past due and restricted; add-ons have no price yet and count for
  nothing; a billed plan without prices (enterprise) has no figure (null). The list adds MRR totals by currency.

### 4. Support access: a ledgered, read-only, one-hour support session

- **Consent:** a Tenant Admin creates a **support consent** in the portal (Settings › Support access): a 16-character
  code (80 bits, the enrollment-code alphabet, stored as SHA-256 only) valid **30 minutes** to be redeemed, single
  use. They read it to Karry support, who types it in the console.
- **Break-glass** (customer unreachable, production incident): an operator gives a **written reason of at least 30
  characters**. It raises an alert (log event and the `KarryBreakGlassSupport` Prometheus alert) and e-mails the
  company's Tenant Admins.
- Either opens a **support session**: one operator, one tenant, **ends 1 hour after it starts** (or earlier when the
  operator or a Tenant Admin ends it). Started, ended and expired sessions are in **the tenant's ledger**
  (`identity.support_session_started.v1` with mode, operator, reason or consent, expiry; `…_ended.v1`).
- **Calls in a session:** the operator's own token plus the header **`Karry-Support-Session: <id>`**. The tenant
  middleware accepts it only for a platform operator whose session is active; the tenant becomes the session's and
  the caller is the built-in **`support` access: read permissions only, every masked field stays masked** (support
  sees what the least privileged office user sees of costs). **Every request** of the session is ledgered
  (`identity.support_request.v1`: method, path template, status). Writes answer **403 `support.read_only`**.
- The portal shows a red, non-dismissable banner in a support session ("Karry support, read only, ends at 14:32"),
  and the company's Settings › Support access lists past and current sessions with an "End now" button.
- **Read-only is the default this ADR decides.** Support that needs to change data asks the customer to do it, or
  uses the customer's own consent for a future write mode (not built). The owner may revisit (D46).
- **As built (2026-10-29, slice 5a, Identity):** `SupportConsent` and `SupportSession` (Identity.Domain), tables
  `identity.support_consents` and `identity.support_sessions`, both under tenant row-level security. The company:
  `POST /api/v1/identity/support/consents` (permission **`identity.support.manage`**, Tenant Admin, privileged, and a
  **recent sign-in**: letting an outsider read the company is a step-up action), `GET /support/sessions` (newest
  100), `POST /support/sessions/{id}/end`. Karry: `GET|POST /api/v1/identity/tenants/{tenantId}/support-sessions`
  and `POST …/{id}/end` (policy `platform-operator`), run in the company's own scope like Billing's operator
  endpoints, so the code is looked up under that company's RLS (another company's code is not found) and every
  change is in its ledger. Decisions made while building:
  - **Expiry needs no separate entry:** `identity.support_session_started.v1` carries `endsAt`, and a session can
    neither be extended nor reopened, so the ledger states the expiry when the session starts. An early end is
    `identity.support_session_ended.v1` (`actor`, `byOperator`).
  - An operator ends **only their own** session (another operator's is "not found"); a Tenant Admin ends any of the
    company's. The company sees the operator's **name**, never their account ID.
  - Break-glass: reason trimmed, 30 to 500 characters. The session is saved and ledgered **before** the e-mail, so a
    mail outage never blocks an incident response. Log events 2200 (opened), 2201 (no administrator reached),
    2202 (e-mail failed). Counter `karry.support.break_glass.sessions` and alert `KarryBreakGlassSupport` (severity
    `page`, [runbook](../06-operations/runbooks/karry-break-glass-support.md)). Each active Tenant Admin with an
    e-mail address gets their own message, in French then English.
  - **Calls in a session (slice 5b) will name the tenant in the header** (`Karry-Support-Session: <tenantId>.<sessionId>`),
    the way a tablet's assertion names its tenant: the session is then looked up under that tenant's RLS, and the
    session tables stay tenant data instead of becoming a platform table without row-level security.
  - Found while testing: a refusal by the framework (a failed `platform-operator` policy, a missing token) answered
    `server.unexpected_error`; it now says `auth.forbidden` / `auth.unauthenticated` ([API standards](../02-architecture/api-standards.md)).
- **As built (2026-10-29, slice 5b, calls in a session):** an operator token **without** `tenant_id` that sends
  `Karry-Support-Session: <tenantId>.<sessionId>` on a tenant endpoint goes through `TenantResolutionMiddleware`'s
  support branch: the tenant is set from the header, the host address check applies, and
  `ISupportSessionAccess.IsActiveAsync` (Identity, under that tenant's RLS) must find **that operator's** session,
  not ended and within its hour; otherwise **403 `support.session_invalid`** (also for a malformed header or a token
  that is not an operator's). A company's own token keeps its own tenant: the header never moves it. Then:
  - `PermissionMiddleware` gives the built-in **`support`** access (`CallerAccess.Support`): every catalogue
    permission whose action is `read`, company-wide, and **every sensitive field `Masked`** (shown as •••, whatever
    the company's own roles see). Anything else answers the usual 403 `auth.forbidden`.
  - Only `GET` and `HEAD` reach the endpoint; any other method answers **403 `support.read_only`**.
  - Every request, refused or not, is ledgered after the response as `identity.support_request.v1`
    (`sessionId`, `method`, `path` = the **route template**, never the IDs or query in the address, `status`). It is
    raised on the session aggregate without changing its row, so the parallel requests of one page never conflict.
    It is written in a scope of its own once the outcome is known; if that write fails, the response has already
    left, so the API logs event **2205** at error level rather than pretending.
- **As built (2026-10-29, slice 5c-1, portal for the company):** **Settings › Support access**
  (`/settings/support`, card shown only with `identity.support.manage`) explains the rules (look only, costs masked,
  one hour, every request in the audit log, emergency access e-mails the Tenant Admins) and lists the company's
  support sessions: when, who, how (**With your code** or **Emergency access** with Karry's written reason), until
  when, and **Looking now** / **Over**. **Create a support code** asks first, then shows the code once, large and
  monospaced, with "Valid until HH:MM"; at expiry the dialog replaces the code with a notice and offers a new one
  (a dead code is never left on screen). **End now** on a running session asks first, then ends it; Karry support
  loses access at its next request. The support error codes have their own messages in both languages.
- **As built (2026-10-29, slice 5c-2, the operator's visit):** the company page of the console has a **Support
  access** card: **Open support** with the company's code (trimmed, upper-cased) or **Emergency access** (a warning
  that the administrators are e-mailed and on-call is paged, then a reason of 30 to 500 characters, counted as typed),
  and the company's sessions, with **Continue** on the operator's own running one. Once the API opens the session
  the tab shows the **company's own portal** (navigation, screens, permissions from `/me/access`, which answers the
  `support` access) on the platform address, with a **fresh query cache** per visit (nothing of the console or of
  another visit shows in it). Every call of the visit carries `Karry-Support-Session: <tenantId>.<sessionId>`; the
  console's own calls never do. A **red banner fixed at the bottom**, which cannot be closed, says whose screens these
  are, that they are read only and when the session ends, with **End support**. The visit stops, with a message and
  back on that company's console page, when the operator ends it, when its hour is over (a timer), or on the first
  `support.session_invalid` (the company ended it). The visit is kept in this tab's `sessionStorage` so a reload
  keeps it (the API checks the session on every call; an expired one is not resumed); another tab starts in the
  console.

### 5. Operators: separate area, mandatory MFA, checked by the API too

- The console is a separate area of the portal (`/operator`, on the platform address with no company), shown only to
  `platform_role=operator`.
- Operator accounts get the realm role `mfa-required` (MFA at every login, as privileged tenant roles). The API also
  requires it: tokens carry the **`amr`** claim (Keycloak's AMR mapper on the portal client), and the
  `platform-operator` policy refuses an operator token without `otp` or `hwk` in it (401 `auth.mfa_required`).
- **As built (2026-10-29, slice 6, operator MFA):** the realm maps `amr` into the app clients' tokens, with a
  reference value on every factor (`pwd`, `otp`, `hwk`, max age = the 10-hour SSO session). The `platform-operator`
  policy adds a `SecondFactorRequirement` (`otp` or `hwk`, ordinal); when it is the only failed requirement the API
  answers **401 `auth.mfa_required`** instead of 403, and the support branch of `TenantResolutionMiddleware` checks
  the same before the session. Found while testing against Keycloak: **enrolling TOTP is a required action, not a use
  of the factor**, so the first token after enrolment says only `pwd`; the portal's error dialog then offers **Sign
  in again** (the step-up pop-up, `prompt=login`, which asks for the code) and reloads the screen's data. Operator
  accounts also hold `mfa-required`, so Keycloak asks for the factor at every sign-in (deploy/keycloak/README.md).
- **As built (2026-10-29, slice 4b, portal):** the operator console is part of the portal app. A token with
  `platform_role=operator` and no `tenant_id` is an operator session (`isOperator`): the router gets
  `{ operator: true }` as context, the root shell renders the operator layout (no company branding, navigation, plan
  or usage banners) and no company call is made (`/tenancy/current`, `/billing/entitlements`, `/flags`,
  `/me/access`). Company screens sit under a pathless `company` route that sends an operator to `/operator`; the
  `operator` route sends anyone else back to `/`. `/operator` lists the companies (`GET /tenancy/tenants`) with the
  MRR totals and creates a company (`POST /tenancy/tenants`; the address is suggested from the name, the first
  administrator is optional and all-or-nothing; the toast says whether the invitation left). The MFA check (`amr`) is
  still to come.
- **As built (2026-10-29, slice 4c, portal):** a company's name in the list opens `/operator/companies/$tenantId`,
  built on the Billing operator API (no new endpoint). The subscription card shows the state and its dates and offers
  only the moves the state machine allows from it (`trial`/`expired` → activate; `active` → missed payment, cancel;
  `past_due`/`restricted` → payment; `suspended` → payment, cancel; none when closed); cancelling asks for
  confirmation. The terms form changes plan, period, sites and extra machines and sends the add-ons, extra features
  and limit overrides back unchanged (the `PUT` replaces the whole terms); a different plan takes its latest version,
  the same plan keeps the company's version; closed subscriptions (`cancelled`, `expired`, `deleted`) cannot change
  terms. Invoices are listed, recorded (number, amount, currency, issue and due dates) and marked paid today. The
  billing refusals an operator can meet and the company-creation ones (`tenant.slug_taken`, `slug_invalid`,
  `slug_reserved`, `name_invalid`) are explained in the error dialog.

## Options considered

- **Filter the ledger by parsing JSON at query time** (`payload::jsonb ->> 'userId'`): no index table, but a per-type
  key list in SQL, a full scan of each tenant's ledger per filter, and no way to fix a mapping later. Rejected.
- **Columns on `audit.ledger_entries`:** existing rows would need an `UPDATE` on an append-only table. Rejected.
- **Impersonation through Keycloak token exchange** (the operator receives a token of the tenant): a second token
  type to secure, harder to make read-only, and Keycloak would hold the audit trail instead of the tenant's ledger.
  Rejected for the header-bound session above.
- **Support with full write access:** faster support, but T-14 is the top privileged-access risk and every write would
  be made "as Karry" in a customer's legal records. Read-only by default.
- **A platform database role bypassing row-level security for the console:** one query instead of N, but one leaked
  connection would expose every tenant. Rejected while N is small.

## Consequences

- ✅ T-14 has a technical control: no operator sees tenant data without a consent code or a written, alerted reason,
  for an hour at most, read-only, every request in the customer's own ledger.
- ✅ The audit viewer and the export use the same masking as every API response.
- ✅ Existing ledgers become searchable through the backfill, without touching the chain.
- ⚠ Every ledgered event must declare its subject (the architecture test enforces it).
- ⚠ The console makes one scope per tenant: about a second per thousand tenants; a nightly read model replaces it
  when that is too slow.
- ⚠ Read-only support cannot fix data for a customer; the owner may want a consented write mode later.

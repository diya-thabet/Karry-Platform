---
title: "ADR-0023: Plans as versioned data, one subscription per tenant, entitlements resolved in-process"
area: adr
tags: [billing, plans, subscriptions, entitlements, usage, limits, state-machine]
source: "Phase 1 sub-phase P1.10"
status: proposed
last_updated: 2026-10-25
---

# ADR-0023 — Plans as versioned data, one subscription per tenant, entitlements resolved in-process

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (P1.10; the owner pre-approved merging green P1.10 PRs, 2026-10-25)
- **Date:** 2026-10-25
- **Deciders:** Product owner, software architect
- **Related:** US-P1-BIL-01 … 05, US-P1-TEN-01 AC1, US-P1-IAM-03 AC4; [business model §2.3–§2.5](../01-product/business-model-and-pricing.md#23-subscription-plans-illustrative-validated-in-phase-0-with-real-prospects);
  [ADR-0019](0019-feature-flags.md) (flags are not entitlements); [ADR-0022](0022-authorization-engine.md) (same
  declare-in-code, fail-closed style); [still wanted from the team N-01](../07-management/still_wanted_from_team.md#1-needed-now-)

## Context

Karry sells four plans per site per month (Starter, Professional, Business, Enterprise) with add-ons and soft limits
(business model §2.3). Sales must be able to change packaging **without a deployment**, customers must keep the terms
they signed until moved, and the product must enforce what is paid for in the API (hiding in the UI is cosmetic).
Two product rules weigh more than revenue: **field capture is never blocked** (a lost shift destroys the anti-fraud
value), and a customer who stops paying keeps access to **their data export**. Until the billing provider arrives
(Phase 10), Karry staff record subscriptions and invoices by hand.

## Options considered

1. **Plans in code** (an enum and a switch per feature). Pros: simple, type-safe. Cons: every packaging change is a
   release; old subscriptions silently get the new terms.
2. **Billing provider as the source of truth** (Stripe products and entitlements). Pros: no catalogue to build. Cons:
   no provider is chosen yet (owner input N-11), a network call per decision, and Merchant-of-Record providers do not
   model limits like "assets per site".
3. **Plans as versioned data in a Karry `Billing` module, entitlements resolved in-process** (chosen). The provider of
   Phase 10 only reports payments into the same state machine.

## Decision

**Plans.** A plan is platform data (`billing.plans`, a global reference table: no tenant data):
`code`, `version`, `name`, `features[]`, `limits{}` and a `priceBook[]` (amount per currency and billing period: a
price book, not a live FX conversion). A published version is **immutable**; changing packaging publishes version
*n + 1*. Subscriptions point at a plan **version** and stay on it until an operator moves them (US-P1-BIL-01 AC2).
Starter, Professional, Business and Enterprise version 1 are seeded from business model §2.3 (**illustrative values**
until pricing validation, owner input N-01; final values are a new version, not code). Feature codes and limit metrics
are declared in code (`Karry.SharedKernel.Entitlements`): a plan naming an unknown feature or metric is refused, like an
unknown permission (ADR-0022).

**Subscriptions.** One per tenant (`billing.subscriptions`, tenant-owned, RLS): plan version, add-ons with quantities,
period (monthly / annual), quantities (sites, extra assets), per-tenant overrides (a feature or limit granted by sales),
and the state. The state machine of business model §2.5 is implemented as a **closed transition table**: any transition
not in the table is refused (TC-P1-BIL-006). Operator actions (activate, record a missed payment, record a payment,
cancel, reactivate) and **time-based** transitions (Trial → Expired at trial end, PastDue → Restricted after 14 days,
Restricted → Suspended after 30, Suspended → Cancelled after 60, Expired → Deleted after 60, Cancelled → Deleted after
90) go through the same state machine; an hourly worker job (`subscription-lifecycle`, also
`Karry.Workers advance-subscriptions`) applies the time-based ones, several at once when it was down. Each operator
action works only from its own states, even where the table has the same edge for another reason: *activate* from
Trial or Expired (within 60 days), *missed payment* from Active, *payment* from PastDue, Restricted or Suspended (a
no-op when nothing is owed), *cancel* from Active or Suspended. Every transition is a tenant
ledger event. Provisioning a tenant creates its subscription (US-P1-TEN-01 AC1): a **30-day trial of the newest
Professional version**, one site, monthly, created by a Tenancy provisioning subscriber in the workers; operators then
change the terms (plan version, period, sites, extra assets, add-ons, overrides). **Deleted** is a state only in P1.10: the crypto-shredding it implies is built with
tenant offboarding, never by this job.

**Invoices** (until Phase 10): manual records (number, amount in decimal + currency, issue and due date, paid date)
attached to the subscription; recording a payment on a PastDue/Restricted/Suspended subscription returns it to Active.

**Entitlements.** `IEntitlementResolver` (BuildingBlocks port, implemented by Billing) resolves plan version +
add-ons + overrides + state into one immutable `TenantEntitlements` value (SharedKernel) per tenant, cached **≤ 60 s**; a change forgets the
cache at once on the instance that made it (same pattern as ADR-0022's access cache). Endpoints declare
`.RequiresFeature("<code>")`; without it the API answers **403** `billing.feature_not_in_plan` (extension `feature`).
403 and not 402: 402 is "reserved for future use" in RFC 9110, poorly supported by clients and proxies, and the problem
`code` already tells the app to show the upgrade card. The check runs **after** the permission check, so a caller
without the permission learns nothing about the plan. The apps read `GET /api/v1/billing/entitlements` (any member) for
`useEntitlement()`; like `/me/access` it only drives what is shown. Limits in it are tenant-wide totals (per-site limits × sites, plus extra assets);
a limit left out is unlimited. An unknown feature code in `.RequiresFeature` fails when the endpoint is mapped, and
without a resolver a gated endpoint is refused (fail closed).

**Usage and limits.** Each module that owns a counted thing reports it through `IUsageSource` (Identity: active office
users; Organization: sites, P1.11; Assets: assets per site, P1.13; storage later). A daily job stores one snapshot per
tenant, metric and day. Limits are **soft**: up to the limit + 10 % grace nothing is blocked (a banner above the
limit, an operator notification); beyond it, only **creating** another counted item is refused (403
`billing.limit_reached`, extensions `metric`, `limit`, `usage`) through `ILimitGuard`, which the creating handler calls
with the live count. **Field capture is never limited**: field users are unlimited on every plan, and capture endpoints
do not call the guard. *As built (slice 4):* metric codes in `Karry.SharedKernel.Entitlements.Metrics` (`office-users`, `sites`,
`assets`, `storage-bytes`; office users = invited or active office accounts, an open invitation holds a seat), the rule
in `SoftLimit`: ceiling = limit + 10 % rounded up (at least one; 20 → 22), states `within` / `over` / `blocked`.
`ILimitGuard.CheckCreateAsync(metric)` returns null or the error (problem members via `Error.With`). Snapshots in
`billing.usage_snapshots` (tenant, metric, UTC day; RLS) daily at 02:30 UTC and at worker start-up; Karry's notice is
alert `KarryTenantOverLimit` (gauge without tenant label) plus worker log event 5002 naming the tenant.
`GET /billing/usage` (`billing.subscription.read`) gives the live lines for "Plan & usage" and the banner.

**State enforcement.** One middleware after authentication reads the state from the cached entitlements:

| State | Office writes | Field capture | Reads | Export |
|---|---|---|---|---|
| Trial, Active, PastDue | ✔ (PastDue: banner) | ✔ | ✔ | ✔ |
| Restricted | ✘ 403 `billing.subscription_restricted`, except billing endpoints | ✔ | ✔ | ✔ |
| Suspended | ✘ `billing.subscription_suspended` | ✘ (read-only) | ✔ | ✔ |
| Cancelled, Expired | ✘ `billing.subscription_inactive` | ✘ | only billing, export, current user | ✔ |

Endpoints are classified by metadata, not by path: `.FieldCapture()` (sync, capture), `.BillingAccess()`,
`.ExportAccess()`; everything else is office. GET/HEAD are reads. Anonymous and platform-operator endpoints are not
subject to a tenant's state. A tenant **without** a subscription (provisioned before P1.10, or whose provisioning subscriber has
not run yet) gets the trial terms of the newest Professional version until it has one: failing closed would lock a new
tenant out during the seconds before its subscription exists. No backfill migration: no production tenant exists before
P1.17, and local stacks are re-seeded.

**Feature flags** stay as ADR-0019 decides (OpenFeature, DB-backed provider, cached ≤ 60 s, changes ledgered);
they never gate plans.

## Consequences

- ✅ Packaging and prices change without a release, and signed terms are never changed under a customer.
- ✅ One place decides features, limits and state; modules declare `.RequiresFeature`, `.FieldCapture` and call
  `ILimitGuard`, nothing else.
- ✅ The Phase 10 provider plugs into the same state machine (its webhooks become the same domain methods).
- ⚠ A plan, override or state change reaches other API instances within 60 s.
- ⚠ Feature codes for modules that do not exist yet (maintenance, plant, finance, AI, edge, telematics, SSO, public
  API) are declared now so the plans can be seeded; each module wires its `.RequiresFeature` when it ships.
- ⚠ Limits are checked against the live count at creation, the snapshot is for reporting: two parallel creations at
  the edge can both pass (accepted: limits are soft by design).

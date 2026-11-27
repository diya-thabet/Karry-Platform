---
title: "Default Role & Permission Matrix"
area: domain
tags: [roles, permissions, rbac]
source: "PHASES_DETAILED_PLAN.md §23"
status: baseline
last_updated: 2026-11-24
---

# Default Role & Permission Matrix

> [Docs index](../../README.md) › [03-domain](../README.md)

Legend: **F** full, **R** read, **W** create/edit own, **A** approve, **M** masked (sensitive financial fields hidden), **—** none. Scope in brackets: (S) assigned sites, (W) assigned warehouses, (T) tenant-wide.

| Capability | Operator | Weighmaster | Fuel Att. | Mechanic | Storekeeper | Controller | Maint. Planner | Site Manager | Commercial | Finance | Executive | Tenant Admin |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Shift entries | W (own) | — | — | — | — | A (S) | R (S) | R/A (S) | — | R (T) | R (T) | R (T) |
| Checklists | W | — | — | R | — | R (S) | R (S) | F (S) | — | — | R | F |
| Continuity exceptions | R (own) | — | — | — | — | A (S) | R | A (S) | — | — | R | R |
| Scale tickets | — | W (S) | — | — | — | A reversals (S) | — | R (S) | R (T) | R (T) | R (T) | R |
| Prices on tickets | — | M or R* | — | — | — | M | — | R | F | F | R | F |
| Customers / orders | — | R (S) | — | — | — | — | — | R (S) | F | R | R | F |
| Fuel dispensing / dips | — | — | W (S) | — | — | A (S) | R | R (S) | — | R | R | R |
| Fuel anomalies | — | — | — | — | — | R (S) | R | F (S) | — | R | F | R |
| Work orders | Breakdown req. | — | — | W (assigned) | R | — | F (S) | A (S) | — | R costs | R | R |
| Components / RUL | — | — | — | R | R | — | F (S) | R (S) | — | R | R | R |
| Inventory movements | — | — | — | Receive sign | F (W) | — | R | R (S) | — | R | R | R |
| Transfers | — | — | — | — | F (W) | — | R | A (S) | — | R | R | R |
| Physical counts | — | — | — | — | W (W) | — | — | R | — | A | R | R |
| Plant graph | — | — | — | — | — | — | R | F (S) | — | R | R | R |
| Production runs / blasts | — | — | — | — | — | A (S) | R | F (S) | R | R | R | R |
| Cost rates, loans, P&L, DSCR | — | — | — | — | — | — | — | M | — | F | R | M/F* |
| Weather & delivery advisory | — | — | — | — | — | — | R | R | F | R | R | R |
| AI Copilot | — | — | — | — | — | — | R | R | R | R | F | Config |
| Users, roles, masking, policies | — | — | — | — | — | — | — | Users (S) | — | — | R | F |
| Billing & plan | — | — | — | — | — | — | — | — | — | R | R | F |
| Audit log / integrity check | — | — | — | — | — | R (S) | — | R (S) | — | R | R | F |

\* configurable per tenant. All roles can be cloned and customised (US-P1-IAM-05).

## As implemented (P1.9)

The matrix above is the target for the whole product. In code it is the union of what each module declares
([ADR-0022](../../adr/0022-authorization-engine.md)): a permission names the default roles that hold it, and the role's
**assignment** gives the scope (tenant-wide, a site, a warehouse). A module brings its rows when it is built.

| Permission | Kind | Privileged | Default roles | Matrix row |
|---|---|---|---|---|
| `identity.users.read` | site | no | Tenant Admin, Executive, Site Manager | Users, roles, masking, policies |
| `identity.users.manage` | site | yes | Tenant Admin, Site Manager | Users (S) |
| `identity.devices.manage` | site | yes | Tenant Admin, Site Manager (their sites) | Site tablets: enrollment codes, device list, revocation (US-P1-OFF-02, ADR-0028) |
| `identity.support.manage` | tenant | yes | Tenant Admin | Support access: support codes for Karry support (recent sign-in), the company's support sessions, "End now" (US-P1-OPS-03 AC3, ADR-0029 §4) |
| `identity.roles.read` | tenant | no | Tenant Admin, Executive | Users, roles, masking, policies |
| `identity.roles.manage` | tenant | yes | Tenant Admin | Users, roles, masking, policies (F) |
| `audit.ledger.read` | site | no | Tenant Admin, Executive, Finance, Controller, Site Manager | Audit log / integrity check (P1.16): on some sites, only the entries of those sites; entries without a site only company-wide |
| `audit.ledger.verify` | site | no | Tenant Admin, Executive, Finance, Controller, Site Manager | Audit log / integrity check |
| `organization.sites.read` | site | no | all 12 default roles (a site role: only its sites) | Every row that works on a site (P1.11: sites list and details) |
| `organization.sites.manage` | tenant | yes | Tenant Admin | Users, roles, masking, policies (F): create, change, archive and restore sites (each site is billed) |
| `organization.shifts.manage` | site | no | Tenant Admin, Site Manager (their sites) | Shift patterns and non-working days of a site (US-P1-ORG-02) |
| `materials.catalog.read` | tenant | no | all 12 default roles | Materials, their density versions and conversions (US-P1-AST-04, US-P1-UNI-01) |
| `materials.catalog.manage` | tenant | yes | Tenant Admin | Create, change, archive materials and set density versions (they change invoiced volumes) |
| `sales.customers.read` | tenant | no | Tenant Admin, Executive, Finance, Commercial, Controller, Site Manager, Weighmaster | Customers (US-P3-SAL-01): the weighbridge needs them to issue tickets |
| `sales.customers.manage` | tenant | no | Tenant Admin, Commercial | Create and change customers, block them with a reason and unblock them, set their credit limit (P3.1) |
| `sales.products.read` | tenant | no | Tenant Admin, Executive, Finance, Commercial, Controller, Site Manager, Weighmaster | Products, price list versions and the price on a date (US-P3-SAL-02); unit prices are the sensitive field `sales.prices.unit-price` |
| `sales.products.manage` | tenant | no | Tenant Admin, Commercial | Create, change, archive and restore products, each a sold material fraction with its sale unit (P3.1) |
| `sales.prices.manage` | tenant | no | Tenant Admin, Commercial, Finance | Create price list versions, change or withdraw them before they come into force (P3.1) |
| `assets.registry.read` | site | no | all 12 default roles | The asset register on the caller's sites, the type catalogue (US-P1-AST-01, US-P1-AST-02) |
| `assets.registry.manage` | site | no | Tenant Admin, Site Manager, Maintenance Planner | Register, change, move and set the status of assets on the caller's sites |
| `fieldops.entries.read` | site | no | Tenant Admin, Executive, Finance, Controller, Site Manager, Maintenance Planner | Shift entries of the caller's sites with every version (ADR-0034 §7; P2.4). Field roles see their own history on the tablet (P2.5) |
| `fieldops.checklists.read` | tenant | no | Tenant Admin, Controller, Site Manager, Maintenance Planner | Pre-start checklist templates and every published version (ADR-0035 §2; P2.6). Tablets get the newest version through sync |
| `fieldops.checklists.manage` | tenant | no | Tenant Admin, Site Manager, Maintenance Planner | Publish the next version of an asset type's checklist; company-wide, so a site assignment alone does not grant it (ADR-0035 §2; P2.6) |
| `fieldops.holds.release` | site | no | Tenant Admin, Site Manager, Maintenance Planner | Release a machine held as unsafe by a critical Not OK, with a reason of 10 to 500 characters; ledgered under the person's name (ADR-0035 §5; P2.6) |
| `integrity.flags.read` | site | no | Tenant Admin, Executive, Controller, Site Manager, Maintenance Planner | Integrity flags of the caller's sites, open and closed (ADR-0036; P2.7). Also reads the company's rule settings |
| `integrity.flags.settle` | site | yes | Tenant Admin, Controller, Site Manager | Settle a continuity gap (RULE-FO-01) of the caller's sites with a reason, approve another person's settlement (never one's own) or decline it; proposing and approving need a recent sign-in; ledgered (ADR-0037 §7; P2.8) |
| `integrity.rules.manage` | tenant | yes | Tenant Admin | Switch the company's integrity rules on or off and set their thresholds, with a recent sign-in; the security rules (RULE-FO-05, 06, 07) cannot be switched off; ledgered (ADR-0036 §1; P2.7) |
| `documents.shift-reports.read` | site | no | Tenant Admin, Executive, Finance, Controller, Site Manager, Maintenance Planner, Operator | Read and issue the shift reports of the caller's sites (ADR-0040; P2.11): the report is masked for its reader with the API's decision, a column the role may not see is absent |
| `notifications.rules.manage` | tenant | yes | Tenant Admin | Switch the company's notice rules on or off (ADR-0039 §2; P2.10 slice 4): switching one off silences an alert for everyone, so it needs a recent sign-in; ledgered with who did it. Every member reads their own notifications and sets their own channel choices without a permission |
| `verification.items.read` | site | no | Tenant Admin, Executive, Controller, Site Manager | The review items of the caller's sites: the queue of entries waiting for a decision, and decided ones (ADR-0037; P2.8). Deciding on them comes with P2.8 slice 2 |
| `verification.items.review` | site | no | Tenant Admin, Controller, Site Manager | Approve or reject the entries of the caller's sites (ADR-0037; P2.8 slice 2): never one's own entry or a machine one drove in the time it covers, as a person through the office ↔ field account link; `If-Match` on the item's version |
| `verification.items.second-approve` | site | yes | Tenant Admin, Site Manager | Approve a correction beyond the four-eyes thresholds (US-P2-VER-04 AC3; P2.8 slice 4): never the person who corrected it, with a recent sign-in |
| `verification.policies.manage` | tenant | yes | Tenant Admin | Publish a review policy version (US-P2-VER-05; P2.8 slice 5): auto-approval, four-eyes thresholds, SLA, escalation role; recent sign-in; ledgered and named on every auto-approval it makes |
| `imports.files.manage` | tenant | yes | Tenant Admin | Excel import (US-P1-ORG-03, ADR-0027): templates, uploads, reports, commit and discard; each kind also needs the permission of the data it writes |
| `tenancy.settings.manage` | tenant | yes | Tenant Admin | Users, roles, masking, policies (F): company settings and logo (P1.11; every member reads them) |
| `billing.subscription.read` | tenant | no | Tenant Admin, Executive, Finance | Billing & plan (P1.10: plan, state, usage, invoices; changes are made by Karry operators until self-service in Phase 10) |

Sensitive fields declared by modules (US-P1-IAM-07; levels per default role, others masked):

| Field | Write | Read | Notes |
|---|---|---|---|
| `sales.customers.credit-limit` | Tenant Admin, Commercial | Finance, Executive | A customer's credit limit (P3.1); a change that leaves it out keeps it |
| `sales.prices.unit-price` | Tenant Admin, Commercial, Finance | Executive, Weighmaster | A unit price in price lists and price previews (P3.1): the plan's `sales.prices.read`; masked for Site Manager and Controller |

Rules that apply to every permission:

- **Kind** says which assignments grant it: *tenant* only from tenant-wide roles; *site* from tenant-wide roles (every
  site) or site roles (that site); *warehouse* also from warehouse roles. A site manager of site A sees the users who
  hold a role on site A, and manages the users whose roles are all on their sites.
- **Granting roles** (threat T-13): only within the caller's sites; never a privileged permission the caller lacks
  there; a privileged role (second factor) only by someone who holds everything it contains. Nobody changes their own
  roles; giving or taking a privileged role needs a sign-in of the last 5 minutes.
- **Second factor:** Tenant Admin, Executive, Finance and Controller (US-P1-IAM-02), and every custom role that is
  privileged.
- **Custom roles** (US-P1-IAM-05): created from scratch or by cloning any role, with any declared permissions and a
  level per sensitive field (a field not set is masked). A custom role is privileged when it holds a privileged
  permission or shows a sensitive field. Default roles keep their permissions; their field levels are company policy.
  A role someone holds cannot be deleted. Changes touching privileged rights or showing a field need a sign-in of the
  last 5 minutes, and a role cannot gain a privileged permission its author lacks company-wide.

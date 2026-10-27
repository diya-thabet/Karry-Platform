---
title: "Default Role & Permission Matrix"
area: domain
tags: [roles, permissions, rbac]
source: "PHASES_DETAILED_PLAN.md §23"
status: baseline
last_updated: 2026-10-26
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
| `identity.roles.read` | tenant | no | Tenant Admin, Executive | Users, roles, masking, policies |
| `identity.roles.manage` | tenant | yes | Tenant Admin | Users, roles, masking, policies (F) |
| `audit.ledger.verify` | site | no | Tenant Admin, Executive, Finance, Controller, Site Manager | Audit log / integrity check |
| `organization.sites.read` | site | no | all 12 default roles (a site role: only its sites) | Every row that works on a site (P1.11: sites list and details) |
| `organization.sites.manage` | tenant | yes | Tenant Admin | Users, roles, masking, policies (F): create, change, archive and restore sites (each site is billed) |
| `organization.shifts.manage` | site | no | Tenant Admin, Site Manager (their sites) | Shift patterns and non-working days of a site (US-P1-ORG-02) |
| `materials.catalog.read` | tenant | no | all 12 default roles | Materials, their density versions and conversions (US-P1-AST-04, US-P1-UNI-01) |
| `materials.catalog.manage` | tenant | yes | Tenant Admin | Create, change, archive materials and set density versions (they change invoiced volumes) |
| `assets.registry.read` | site | no | all 12 default roles | The asset register on the caller's sites, the type catalogue (US-P1-AST-01, US-P1-AST-02) |
| `assets.registry.manage` | site | no | Tenant Admin, Site Manager, Maintenance Planner | Register, change, move and set the status of assets on the caller's sites |
| `imports.files.manage` | tenant | yes | Tenant Admin | Excel import (US-P1-ORG-03, ADR-0027): templates, uploads, reports, commit and discard; each kind also needs the permission of the data it writes |
| `tenancy.settings.manage` | tenant | yes | Tenant Admin | Users, roles, masking, policies (F): company settings and logo (P1.11; every member reads them) |
| `billing.subscription.read` | tenant | no | Tenant Admin, Executive, Finance | Billing & plan (P1.10: plan, state, usage, invoices; changes are made by Karry operators until self-service in Phase 10) |

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

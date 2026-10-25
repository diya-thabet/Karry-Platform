---
title: "ADR-0022: Authorization engine: permissions declared in code, scopes per assignment, masking in the serializer"
area: adr
tags: [authorization, rbac, abac, masking, step-up, security]
source: "Phase 1 sub-phase P1.9"
status: accepted
last_updated: 2026-10-25
---

# ADR-0022 — Authorization engine: permissions declared in code, scopes per assignment, masking in the serializer

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner approved the P1.9 pull requests #50–#55; built and verified 2026-10-25)
- **Date:** 2026-10-24
- **Deciders:** Product owner, software architect
- **Related:** [ADR-0012](0012-identity-authorization.md) (which this ADR details, not replaces); US-P1-IAM-04 … 07;
  threats T-10, T-13 ([threat model](../02-architecture/threat-model.md)); [role matrix](../03-domain/catalogues/roles-and-permissions.md)

## Context

ADR-0012 decided *that* authorization lives in Karry (roles = permission sets, scopes tenant/site/warehouse, field rules
READ/WRITE/MASKED/HIDDEN in API serialization). P1.9 has to decide *how*, in a way that stays correct while 15 more
modules are added over the next phases, each with its own endpoints, its own sensitive fields and its own rows in the
role matrix. The two failure modes to design out are: **an endpoint that forgets its check** (it is open), and **a
response or export that forgets a masked field** (it leaks prices or wages, threat T-10).

## Options considered

1. **ASP.NET Core policies only** (one policy per permission, `[Authorize(Policy = …)]`). Pros: standard. Cons: no
   notion of scope (site lists, out-of-scope 404), the default for an endpoint without a policy is "any signed-in user"
   (open), field masking is not covered.
2. **External policy engine** (OPA, Cerbos, OpenFGA). Pros: policy as data, audit tooling. Cons: a network hop on every
   request and on every serialized object for masking, one more service to operate and secure, the tenant's role editor
   would have to write into it; overkill for one monolith (ADR-0001).
3. **In-process engine** (chosen): permissions and sensitive fields **declared by each module in code**, role
   assignments resolved per request by the Identity module, one middleware in front of every tenant endpoint, masking in
   the JSON contract of the API.

## Decision

**Declarations.** Each module's `IModule` lists its `Permissions` (`module.resource.action`, scope kind tenant / site /
warehouse, privileged or not, the default roles that hold it) and its `SensitiveFields` (`module.resource.field`, the
default level per default role and a fallback for other roles). The vocabulary is in `Karry.SharedKernel.Authorization`;
the host builds an `AuthorizationCatalog` at start-up and **refuses to start** on a duplicate code, a code of another
module or an unknown role. The role matrix is therefore the union of what the modules declare: a new module brings its
own rows, and the default roles get its permissions without a data migration.

**Default and custom roles.** The 12 default roles are defined by the catalogue in code: they cannot be deleted or have
their permissions edited, so a release that adds permissions reaches every tenant. A tenant customizes by **cloning**
(custom role = stored permission set). Field levels are tenant configuration for every role, default ones included.
As built (P1.9 slice 4a): a custom role (`identity.roles`) has the code `custom-<id>`, so renaming it never touches the
users who hold it; it stores its permissions and the field levels it sets (a field it does not mention gets the field's
fallback, masked by default). A default role stores only the field levels that differ from the catalogue
(`identity.default_role_settings`). Permissions a module no longer declares are ignored; a role the tenant no longer
has grants nothing and counts as privileged for the second factor (fail closed). A custom role is **privileged** when
it holds a privileged permission or shows a sensitive field (read or write). A role someone holds cannot be deleted.

**Scope comes from the assignment.** A user holds a role tenant-wide, on a site or on a warehouse. A permission of kind
*tenant* is granted only by tenant-wide assignments; *site* by tenant-wide (all sites) or site assignments (that site);
*warehouse* by tenant-wide, site (the warehouses of that site) or warehouse assignments. Handlers filter lists by the
caller's `ScopeSet` and answer **404** for an object outside it (US-P1-IAM-06 AC2).

**Enforcement.** `PermissionMiddleware` runs after tenant resolution on every endpoint that requires a tenant. An
endpoint must declare `.RequirePermission(code)` or, as a reviewed exception, `.AllowAnyTenantMember()`; **an endpoint
that declares neither is refused with 403 `auth.permission_undeclared`** (fail closed), as is a permission no module
declares. The caller's access (assignments resolved by the Identity module through `IAccessResolver`, cached ≤ 30 s
per instance like deactivation) is stored for the request in `ICallerAccess`.

**Step-up.** Privileged changes declare `.RequireRecentSignIn()`: the token's `auth_time` must be at most 5 minutes old,
else 401 `auth.step_up_required` with the RFC 9470 challenge (`insufficient_user_authentication`, `max_age`). The app
re-authenticates in a pop-up (Keycloak `max_age=0`) and retries, so the form the admin filled is not lost. The check
runs after the permission check, so it reveals nothing to a caller without the permission. When only the change itself
tells whether it is privileged, the handler decides with `StepUp.Check` and returns the same 401; a module-wide endpoint
filter adds the challenge header. For roles, a privileged change is one that adds or removes a privileged permission,
raises a field to read or write, or deletes a privileged role.

**Masking.** A property marked `[Sensitive("code")]` is filtered in the API's JSON contract (System.Text.Json
contract modifier): READ/WRITE → sent; MASKED → **not sent**, its name listed in the object's `maskedFields` (the UI
shows `•••` "hidden by policy"); HIDDEN → not sent and not listed. The level is the highest among the caller's roles
that apply to the object's site (`ISiteScoped`); for an object without a site only tenant-wide roles count, so a
forgotten `ISiteScoped` over-masks instead of leaking. Export and PDF writers ask the same `CallerAccess`. Because the masked
value is never written (no placeholder in the value's place), a masked field cannot leak through a type confusion, and
the OpenAPI contract marks every sensitive property (`x-karry-sensitive`) so contract reviews see it.

**Anti-escalation (T-13).** Nobody changes their own roles; only tenant-wide holders grant tenant-wide assignments; a
caller cannot grant a role containing a privileged permission they do not hold themselves, nor make a role gain one
they do not hold tenant-wide; every change of roles, assignments and field levels is ledgered with before/after (codes
and IDs only). When roles make a second factor required (a user's new roles, or a held custom role that becomes privileged),
Keycloak's `mfa-required` is set and the user's sessions end; when it is no longer required, it is removed. A role change forgets the cached access of the whole tenant at once on the instance that made it (a
per-tenant generation in the cache key); other instances follow within the 30 s cache lifetime.

## Consequences

- ✅ Forgetting a declaration closes an endpoint instead of opening it; tests list every endpoint and its declaration.
- ✅ The role matrix lives next to the code that needs it and grows with each module; no per-tenant data migration
  when a module adds permissions.
- ✅ One place masks API responses; export writers reuse the same decision.
- ⚠ Default roles' permissions change with releases: a release note must list matrix changes (tenants who want a frozen
  set clone the role).
- ⚠ A role or policy change reaches other API instances within 30 s (cache); the instance that made the change applies
  it at once. Same bound as deactivation (ADR-0012 notes).
- ⚠ A sensitive field must be marked `[Sensitive]` by the developer: a naming check on API objects (price, cost, wage,
  margin, loan …) and the generated masking suite (TC-P1-IAM-005) catch the omission.

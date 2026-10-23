---
title: "ADR-0012: Keycloak for identity; in-app RBAC + ABAC + field masking"
area: adr
tags: [identity, keycloak, rbac, abac, masking, security]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-24
---

# ADR-0012 — Keycloak for identity; in-app RBAC + ABAC + field masking

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** US-P1-IAM-01..07; US-P9-IAM-01

## Context

Karry needs standards-based login (OIDC), MFA for privileged roles, enterprise SSO later, offline-capable field sessions, and fine-grained, tenant-configurable authorization including hiding financial fields from some roles.

## Options considered

1. Build authentication in-house — **Pros:** Full control. **Cons:** Security risk; reinventing MFA, SSO and brute-force protection.
2. Hosted IdP (Auth0, Entra External ID) — **Pros:** No ops. **Cons:** Per-user cost; data residency; vendor lock-in.
3. **Keycloak (self-hosted) + in-app authorization engine** (chosen) — **Pros:** Standards, SSO and MFA built in, residency control; authorization kept close to the domain. **Cons:** Keycloak must be operated.

## Decision

Keycloak provides OIDC (Authorization Code + PKCE), MFA, brute-force protection and federation (SAML/OIDC) for Enterprise. Authorization lives in Karry: roles = permission sets; permissions = (resource, action, scope); scopes = tenant/site/warehouse (ABAC); field rules = READ/WRITE/MASKED/HIDDEN applied **in API serialization**, exports, PDFs and AI tools. Field users on shared tablets unlock with employee ID + PIN against a time-boxed offline session.

## Tenant ↔ Keycloak mapping (update, P1.8, 2026-10-24)

- **One realm (`karry`) with Keycloak Organizations**, one organization per tenant (alias = tenant slug). This scales
  to thousands of tenants in one realm, keeps one set of clients and flows, and leaves the door open to per-tenant
  identity providers (Organizations link IdPs to an organization) for Enterprise SSO.
- The tenant claim is **not** derived from organization membership: each user carries an admin-only `tenant_id`
  attribute mapped to the `tenant_id` claim, so a token always names exactly one tenant (what
  `TenantResolutionMiddleware` requires) even if a person later belongs to several organizations.
- **Dedicated realm** remains the option for Enterprise / regulated tenants (silo model); same clients and flows,
  imported from the same file.
- MFA for privileged roles is enforced **in Keycloak** (realm role `mfa-required`, conditional browser flow), so no
  token is ever issued without the second factor; Karry decides who holds the role.
- Implementation and reasons for every setting: [deploy/keycloak/README.md](../../deploy/keycloak/README.md);
  [security §14.1.1](../02-architecture/security-identity-tenancy.md#1411-as-implemented-p18-deploykeycloak).

## Consequences

- ✅ Enterprise SSO possible without redesign; masking enforced server-side.
- ⚠ Keycloak upgrades and realm configuration are managed as code.

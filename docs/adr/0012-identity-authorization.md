---
title: "ADR-0012: Keycloak for identity; in-app RBAC + ABAC + field masking"
area: adr
tags: [identity, keycloak, rbac, abac, masking, security]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0012 — Keycloak for identity; in-app RBAC + ABAC + field masking

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
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

## Consequences

- ✅ Enterprise SSO possible without redesign; masking enforced server-side.
- ⚠ Keycloak upgrades and realm configuration are managed as code.

---
title: "ADR-0041: The Karry website is a separate product and repository"
area: adr
tags: [adr, phase-2, p2-13, website, landing-page, on-premises, deployment, boundaries]
source: "Owner direction 2026-11-27: the landing page must not live in the ERP, because customers may install Karry on their own servers; P2.13 slice plan"
status: accepted
last_updated: 2026-11-27
---

# ADR-0041: The Karry website is a separate product and repository

> [Docs index](../README.md) › [ADRs](README.md) · [P2.13 slice plan](../04-phases/phase-02-field-operations/p2.13-landing-page-plan.md)

- **Status:** Accepted (2026-11-27, owner direction)
- **Date:** 2026-11-27

## Context

P2.13 adds a public landing page: what Karry does, in French, English and Arabic, with **Se connecter** and **Créer un
compte** at the top right. The first draft of the plan put it in this monorepo as `src/frontend/apps/site`, sharing
`@karry/ui` and `@karry/i18n`.

Karry is also **installed on customers' own servers** (on-premises). Anything in this repository is part of what we
build, version, scan and ship to them. A marketing site has no place in a customer's installation: it would be shipped,
hosted and patched by people who never use it, its copy would freeze at the platform's release, and its campaigns would
pass through the platform's change control.

## Decision

1. **Its own repository and product: `karry-website`.** It is never part of a platform build, image, Helm chart or
   on-premises bundle. It has its own release life (deployed when the copy changes, not on the platform's versions).
2. **It imports nothing from the platform.** No `@karry/*` package, no shared build. Its few building blocks (dialog,
   text field), its tokens and its catalogs (FR, EN, AR only) are its own. The brand files (the K, the logo, the facets
   and arrows) are copied; the platform's sign-in theme (`deploy/keycloak/themes/karry/login/img`) holds the other copy.
   A logo change, which is rare, is made in both.
3. **The only link is addresses.** *Se connecter* asks for the company's identifier, checks it with the platform's
   existing public lookup `GET /api/v1/tenancy/public/{slug}` (public CORS policy: any origin, `GET` only, no
   credentials, already tested in `TenancyApiTests`), then sends the person to `https://<company>.<domain>/`, whose
   portal leads to the Karry sign-in page. **The platform does not know the website exists**: nothing changes in this
   repository for it, and an on-premises installation works the same with or without it.
4. **The same engineering rules** as the platform: tests locally and in CI (unit, component, axe, browser tests on the
   production build), dependencies pinned and at least 7 days old (`pnpm-workspace.yaml`), actions pinned by SHA, a
   strict page policy, WCAG 2.2 AA, every animation off with `prefers-reduced-motion`, no third-party tracker.
5. **Static hosting.** The build is plain files (one prerendered page per language, `robots.txt`, `sitemap.xml`) for
   any static host or CDN. Choosing and paying for that host is deferred with the other cloud items (P1.17); until then
   it runs locally and its CI uploads the build.
6. **Sign-up** stays an owner choice (P2.13 plan §3). If it becomes a trial request form (option A), the endpoint that
   receives it belongs to Karry's own operations (the hosted service), not to the platform that customers install.

## Consequences

- An on-premises customer gets only the ERP; their people sign in at their own portal address. The website concerns the
  hosted service.
- Two repositories to keep: the website's CI is small (one job, about 3 minutes) and its pull requests do not run the
  platform's pipelines, and the other way round.
- Brand files are duplicated on purpose (decision 2); the cost is one extra copy on a rare change, the gain is no
  coupling between the two release lives.
- The website depends on one platform contract: the public lookup's address and its `{ slug, name, … }` answer. Changing
  that route is a breaking change for the website and is treated as such (it is already public for the portal and the
  sign-in page).

## Alternatives considered

| Option | Why not |
|---|---|
| `apps/site` in this monorepo, excluded from on-premises bundles | Still built, scanned and versioned with the ERP; the exclusion is a rule someone must remember in every packaging change; the copy follows the platform's release pace |
| A website-builder SaaS | A third-party tracker and a subscription; the brand and the sign-in flow need code anyway |
| A shared `@karry/brand` package published for both | A package registry and versioning for a handful of SVG files; worth it only if more products appear |

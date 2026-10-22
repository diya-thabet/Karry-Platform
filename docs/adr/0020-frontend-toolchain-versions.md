---
title: "ADR-0020: Frontend toolchain: Node 24 LTS, pnpm, TypeScript 6.0 until the lint ecosystem supports 7"
area: adr
tags: [frontend, toolchain, typescript, node, pnpm, eslint, versions]
source: "Phase 1 sub-phase P1.0"
status: proposed
last_updated: 2026-10-22
---

# ADR-0020 — Frontend toolchain: Node 24 LTS, pnpm, TypeScript 6.0 until the lint ecosystem supports 7

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed
- **Date:** 2026-10-22
- **Deciders:** Product owner, software architect
- **Related:** US-P1-PLT-01, US-P1-PLT-03, [guidelines §6.3](../00-llm/llm_guidelines.md#63-typescript--react)

## Context

The Phase 0 PWA spike used TypeScript 7.0 (native compiler) and only ran `tsc`. Phase 1 adds linting with **type-aware rules** (for example no floating promises, which matters for offline sync code), Storybook and Vitest.

**Facts verified on 2026-10-22:**

- `typescript-eslint` 8.72 (latest) declares `typescript >=4.8.4 <6.1.0`: it does **not** support TypeScript 7 yet. The latest 6.x is **6.0.3**.
- Node.js: latest **LTS is 24** ("Krypton", 24.22.1); 26 is "Current" and becomes LTS later. The owner's machine has Node 22 (maintenance LTS).
- pnpm: latest major is **12** (12.15.0); the spike used 9.15.9.

## Options considered

1. **TypeScript 6.0.x everywhere + typescript-eslint typed rules** (chosen): full ecosystem compatibility. **Cons:** slower type checking than TS 7.
2. TypeScript 7 + Biome (no typed lint from typescript-eslint). **Cons:** loses the most valuable type-aware rules; Biome's type-aware rules are partial.
3. TypeScript 7 for `tsc` + TS 6 for ESLint side by side. **Cons:** two compilers can disagree; confusing.

## Decision

- **Node 24 LTS** pinned in `.nvmrc` / `package.json#engines` and in CI; move to Node 26 within 3 months of it becoming LTS (Renovate PR + full CI).
- **pnpm 12** pinned via `packageManager` (Corepack).
- **TypeScript 6.0.x** (exact pin) for all workspace packages, `strict` + `noUncheckedIndexedAccess`; ESLint 10 flat config with `typescript-eslint` type-aware rules.
- **Upgrade trigger to TypeScript 7:** a `typescript-eslint` release that supports it, then one PR with a green full pipeline. The Phase 0 spike keeps its own pins (it is frozen evidence).

## Consequences

- ✅ Type-aware linting from day 1 on offline/sync code.
- ⚠ Slightly slower `typecheck` until TS 7 is adopted.
- ⚠ The owner's machine needs Node 24 (`nvm install 24`).

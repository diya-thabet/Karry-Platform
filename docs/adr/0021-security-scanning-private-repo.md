---
title: "ADR-0021: Security scanning with gitleaks, Trivy and Semgrep CE (CodeQL needs a paid licence on private repositories)"
area: adr
tags: [security, ci, sast, secrets, sca, codeql, semgrep, trivy, gitleaks, licensing]
source: "Phase 1 sub-phase P1.2"
status: accepted
last_updated: 2026-10-23
---

# ADR-0021 — Security scanning with gitleaks, Trivy and Semgrep CE

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-23: "keep it with the free tools")
- **Date:** 2026-10-22
- **Deciders:** Owner (accepted 2026-10-23), software architect
- **Related:** US-P1-PLT-03, [guidelines §10](../00-llm/llm_guidelines.md#10-security-policy), [technology stack](../02-architecture/technology-stack.md)

## Context

The plan listed **CodeQL** (GitHub Advanced Security) for static analysis. The repository `diya-thabet/Karry-Platform` is **private**. CodeQL's licence allows use on private repositories only with **GitHub Code Security** (paid per active committer); without it, code scanning cannot be enabled and running the CodeQL CLI on private commercial code is not permitted. The pipeline still needs: secret detection, vulnerable-dependency detection, IaC/container misconfiguration checks and SAST.

## Options considered

1. **gitleaks (MIT) + Trivy (Apache-2.0) + Semgrep Community Edition (LGPL engine, Semgrep Registry rules under the Semgrep Rules License, internal use allowed)** (chosen). **Pros:** free and licence-compatible for private commercial code; run as checksum-verified binaries or a digest-pinned image, no third-party GitHub Action. **Cons:** Semgrep CE is intra-procedural (less deep than CodeQL data-flow); no GitHub security tab without GHAS.
2. Buy GitHub Code Security now. **Pros:** CodeQL data-flow analysis, Dependabot alerts UI, secret push protection. **Cons:** recurring cost before revenue.
3. Make the repository public. Rejected: commercial product.

## Decision

- **Secrets:** gitleaks over the full git history on every PR (`ci / security`).
- **Dependencies and misconfiguration:** Trivy `fs` (NuGet lock files, pnpm lock file; Dockerfile/Kubernetes/Terraform misconfig) failing on HIGH/CRITICAL with a fix available; a nightly run with a fresh database plus `dotnet list package --vulnerable` and `pnpm audit`.
- **SAST:** Semgrep CE with the `p/csharp`, `p/typescript`, `p/react`, `p/owasp-top-ten`, `p/dockerfile`, `p/python` rulesets, `--error`, metrics off; suppressions only inline with a written reason (`nosemgrep: <rule> — why`).
- Roslyn security analyzers (CA rules at `latest-recommended`) remain part of the build.
- **Revisit trigger:** first paying customers or a security questionnaire that asks for it → enable GitHub Code Security (CodeQL + push protection) in addition.

## Consequences

- ✅ Every PR is scanned for secrets, vulnerable dependencies and common vulnerability patterns at no cost.
- ✅ The first scan already found real hardening gaps (pnpm supply-chain settings), now fixed.
- ⚠ Weaker inter-procedural analysis than CodeQL; compensated by code review checklists and the threat model tests.

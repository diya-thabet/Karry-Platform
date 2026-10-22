---
title: "ADR-0013: AI copilot via tool-calling over a governed semantic layer, with provider abstraction"
area: adr
tags: [ai, llm, copilot, semantic-layer, security]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0013 — AI copilot via tool-calling over a governed semantic layer, with provider abstraction

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** [ai-copilot-architecture.md](../02-architecture/ai-copilot-architecture.md); Phase 8

## Context

Executives want natural-language answers. LLMs can invent numbers and, given SQL access, can leak data across roles or tenants. Some customers will forbid external LLM processing.

## Options considered

1. Text-to-SQL on production — **Pros:** Flexible. **Cons:** Hallucinated queries; RBAC/masking bypass risk.
2. **Tool-calling over a semantic layer; numbers only from deterministic engines** (chosen) — **Pros:** Correct numbers with citations; RBAC enforced by the layer. **Cons:** Tools and metrics must be curated.

## Decision

The LLM calls tools (`get_metric`, `list_flags`, `get_dscr`, …) that query a semantic layer of named metrics and dimensions which enforces tenancy, scope and masking. Answers cite sources; charts are rendered only from tool data. An AI Gateway abstracts providers (Claude by default, an OpenAI-compatible self-hosted model for sovereign tenants); tenants can disable AI entirely. Prompts and tools are versioned and gated by an evaluation harness (≥ 90 % numeric exactness, 100 % citations, 0 leaks). No write tools in v1.

## Consequences

- ✅ Trustworthy, auditable answers; provider can change without touching the domain.
- ⚠ Each new question type may need a new tool or metric.

---
title: "Phase 8 — AI Copilot"
area: phases
tags: [phase-8, ai, copilot]
source: "PHASES_DETAILED_PLAN.md §15"
status: baseline
last_updated: 2026-10-21
---

# Phase 8 — AI Copilot

> [Docs index](../../README.md) › [04-phases](../README.md)

**Duration:** 5 weeks (W45–W49) · **M4 — Intelligence Release**

## 15.1 Objectives & business value

Executives get **answers in plain French or English, with proof**, plus automatic summaries and explanations. This makes Karry stand out in sales demos and justifies the Business tier and the AI add-on. The deterministic engines compute every number; the LLM explains them and never invents them.

## 15.2 Scope

**In:** AI Gateway (provider abstraction, policies, budgets), semantic layer (named metrics, dimensions, filters, RBAC/masking), tool set, conversation UI with citations & inline charts, executive daily/weekly summaries, anomaly & grouping explanations, AI audit, evaluation harness, red-teaming, per-tenant AI controls (on/off, provider, budget, data residency).
**Out:** AI write actions (creating WOs, approving), document RAG (P9), voice (P11).

## 15.3 Epics

| Epic | Title |
|---|---|
| EP-P8-01 | AI Gateway & governance |
| EP-P8-02 | Semantic layer |
| EP-P8-03 | Tools |
| EP-P8-04 | Copilot conversation experience |
| EP-P8-05 | Automated summaries & explanations |
| EP-P8-06 | Evaluation, safety & cost control |

## 15.4 User stories & acceptance criteria

### EP-P8-01 AI Gateway & governance

**US-P8-AI-01 — AI Gateway** · *Persona: Engineer* · Must · M
- AC1: Internal interfaces `IChatModel`, `IEmbeddingModel`; adapters for Anthropic Claude (default) and an OpenAI-compatible endpoint (self-hosted open-weight via vLLM); configured per tenant.
- AC2: Default model routing: `claude-sonnet-5-5` for Q&A, `claude-haiku-5-5` for intent classification & short summaries, `claude-opus-5-5` for monthly deep analysis. Routing is configuration, not code.
- AC3: Timeouts, retries, circuit breaker, streaming responses to UI.
- AC4: Provider keys stored in vault; never sent to client.

**US-P8-AI-02 — Tenant AI policy** · *Persona: Tenant Admin* · Must · S
- AC1: Admin can enable/disable AI, choose allowed provider (external vs sovereign), set monthly budget (within plan quota), choose which roles can use the copilot.
- AC2: When disabled, no tenant data is ever sent to any LLM (test proves).

### EP-P8-02 Semantic layer

**US-P8-AI-03 — Metric catalogue** · Must · M
- AC1: Named metrics with definitions (FR/EN), units, formula reference, owning module: e.g., `fuel.litres`, `fuel.lph`, `fuel.anomalies.count`, `production.tonnes`, `yield.pct`, `maintenance.availability`, `maintenance.rul_days`, `finance.net_cash_margin`, `finance.dscr`, `finance.cost_per_tonne`, `weighbridge.tonnes_sold`, `inventory.stock_value`, `integrity.flags.open`.
- AC2: Dimensions: site, asset, asset type, product, customer, warehouse, period (day/week/month), shift.
- AC3: Every query through the semantic layer applies tenant, scope and field masking; masked metrics return "not permitted".
- AC4: The same semantic layer powers dashboards' "explain this number", so there is one definition per metric.

### EP-P8-03 Tools

**US-P8-AI-04 — Tool set v1** · Must · M
- AC1: Tools: `get_metric(metric, dims, filters, period)`, `compare_metric(metric, a, b)`, `get_asset_profile(asset)`, `list_flags(type, period, site)`, `get_flag_detail(id)`, `get_rul_overview(site)`, `get_grouping_proposals(site)`, `get_dscr(asset|portfolio, period)`, `get_delivery_advisory(order|quantity, date, site)`, `find_entities(name)` (fuzzy search for assets, customers, sites).
- AC2: Tools return structured JSON with `sources` (record IDs, links, period, filters) used for citations.
- AC3: Tool outputs size-limited; pagination when large.

### EP-P8-04 Copilot experience

**US-P8-AI-05 — Ask the copilot** · *Persona: Executive* · Must · M
As an owner, I want to ask "Did loader CAT-966 cover its loan last month?" and get a precise answer with proof, so that I decide without waiting for a report.
- AC1: Chat panel in portal (and mobile web) in FR/EN (answers in the question's language).
- AC2: Answers include numbers only from tool outputs, citations (clickable to dashboards/records), and optional inline chart (ECharts spec generated from tool data, never from LLM-invented numbers).
- AC3: When data is insufficient or not permitted, the copilot says so explicitly.
- AC4: Suggested questions per role; conversation history per user (12 months retention, deletable).
- AC5: Thumbs up/down + comment feedback stored for evaluation.

**US-P8-AI-06 — Contextual "Ask about this"** · Should · S
- AC1: On any dashboard widget or flag, "Ask about this" opens the copilot pre-filled with the context (metric, filters, record).

### EP-P8-05 Summaries & explanations

**US-P8-AI-07 — Executive daily/weekly briefing** · Must · M
- AC1: Scheduled per user preference; content: production vs target, top integrity flags, fuel anomalies, upcoming critical replacements, DSCR watch list, weather risk next 7 days; each item cited.
- AC2: Delivered by email and in-app; WhatsApp short version optional.

**US-P8-AI-08 — Anomaly explanation** · Must · S
- AC1: For fuel anomalies, continuity exceptions and ticket flags, an "Explain" button produces a narrative using flag details and related records, listing plausible causes and suggested checks (never accusing a person by name as guilty).

**US-P8-AI-09 — Maintenance grouping explanation** · Should · S
- AC1: Grouping proposals (P4) explained in plain language with trade-offs.

### EP-P8-06 Evaluation, safety & cost

**US-P8-AI-10 — Evaluation harness** · Must · M
- AC1: Golden set ≥ 150 questions (FR/EN) with expected tool calls and expected numeric answers on a fixed evaluation tenant.
- AC2: CI job runs the set on prompt/model/tool changes; metrics: tool-selection accuracy, numeric exactness, citation presence, refusal correctness, language match, latency, cost.
- AC3: Release gate: ≥ 90 % numeric exactness, 100 % citation presence on numeric answers, 0 cross-scope leaks.

**US-P8-AI-11 — Safety guardrails** · Must · M
- AC1: Prompt-injection resistance: user-entered text (remarks, comments) passed to the model is delimited and treated as data; system prompts instruct to ignore embedded instructions; tested with red-team set.
- AC2: Output filter blocks rendering of content outside allowed formats (no HTML/script).
- AC3: No write tools in v1.

**US-P8-AI-12 — AI audit & cost tracking** · Must · S
- AC1: Each interaction stores: user, tenant, question, tools called with parameters, tool outputs hashes, answer, model, tokens, cost, latency.
- AC2: Usage metering for AI tokens per tenant (billing); budget exhaustion → graceful message & admin notification.

## 15.5 Technical scope

- Module `AiCopilot` (Conversation, Message, AiAuditRecord, Briefing), `SemanticLayer` (metric registry in code + metadata, query compiler to SQL/read models with policy enforcement).
- AI Gateway implemented inside the monolith behind an interface. It is the first candidate for extraction (security isolation), see [§3.6](../../02-architecture/modular-monolith-decision.md#36-extraction-triggers-when-we-will-create-a-microservice).
- Streaming via Server-Sent Events.
- Prompt and tool definitions are versioned files in repo; changes go through evaluation gate.

## 15.6 Test plan (Phase 8)

| Test ID | Description | Expected |
|---|---|---|
| TC-P8-AI-001 | Golden set run | ≥ 90 % numeric exactness |
| TC-P8-AI-002 | Question about masked metric (role without permission) | Refusal, no number leaked |
| TC-P8-AI-003 | Tenant B entity name in tenant A question | Not found |
| TC-P8-AI-004 | Injection in operator remark ("ignore previous instructions…") | Ignored, answer unaffected |
| TC-P8-AI-005 | AI disabled tenant | No outbound LLM calls (network assertion) |
| TC-P8-AI-006 | Budget exhausted | Graceful message, admin notified |
| TC-P8-AI-007 | FR question → FR answer, EN → EN | Language match |
| TC-P8-AI-008 | Provider outage | Fallback provider or clear error, no hang |
| TC-P8-AI-009 | Chart generation | Chart data equals tool data exactly |
| TC-P8-AI-010 | Briefing content citations | 100 % items cited |
| TC-P8-AI-011 | Load: 100 concurrent conversations | p95 first token < 3 s |

**Red-team exercise:** internal team attempts cross-tenant leakage, masked data extraction, injection via every free-text field, jailbreaks; findings fixed before release.

**UAT:** pilot executives use the copilot for 2 weeks; satisfaction survey; feedback reviewed.

## 15.7 Deliverables

AI Gateway, semantic layer, tools, copilot UI, briefings, explanations, evaluation harness + reports, AI policy settings, AI usage metering, AI transparency documentation for customers (what data is sent where).

## 15.8 Demo script

1. "Le chargeur CAT-966 a-t-il couvert son prêt le mois dernier ?" → answer with DSCR, numbers, chart, citations.
2. "Which warehouse transferred jaw plates to Site B last month?" → list with waybill links.
3. Click "Explain" on a fuel anomaly.
4. Show the morning briefing email.
5. Show admin AI policy: switch to sovereign model / disable.

## 15.9 Exit gate (M4)

- [ ] Eval gate met; red-team findings closed.
- [ ] Pilot executives use it weekly (≥ 3 sessions/week/user over 2 weeks).
- [ ] AI cost per Business site within target ([§2.7](../../01-product/business-model-and-pricing.md#27-unit-economics-targets)).

## 15.10 Risks

| Risk | Mitigation |
|---|---|
| Hallucinated numbers | Numbers only from tools; eval gate; citations required |
| Cost overruns | Quotas, caching, cheaper model routing |
| Customer data concerns | Transparency doc, sovereign option, opt-out |

## 15.11 Commercial outcome

Strong differentiation in demos. The AI add-on is sold to Professional customers and included in Business. Marketing content: "Ask your quarry anything."

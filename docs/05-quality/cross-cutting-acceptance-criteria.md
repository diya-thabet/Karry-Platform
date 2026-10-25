---
title: "Cross-Cutting Acceptance Criteria (CC-01…CC-18)"
area: quality
tags: [acceptance-criteria, cross-cutting]
source: "PHASES_DETAILED_PLAN.md §5"
status: baseline
last_updated: 2026-10-21
---

# Cross-Cutting Acceptance Criteria (CC-01…CC-18)

> [Docs index](../README.md) › [05-quality](README.md)

These criteria are **implicitly part of every user story**. QA verifies them without them being repeated in each story.

| ID | Criterion |
|---|---|
| **CC-01 Tenant isolation** | Data created by tenant A is never readable/writable by tenant B through any endpoint, export, search, report, notification or AI answer. An automated isolation test exists for every new endpoint. |
| **CC-02 Authorization** | Every endpoint declares a permission and scope. Users without permission get `403` (or `404` where existence must be hidden). UI hides unavailable actions, but the API enforces. |
| **CC-03 Field masking** | Fields configured as MASKED/HIDDEN for a role are masked in API responses, exports, PDFs and AI tool outputs, not only in UI. |
| **CC-04 Audit** | Every create/change/approve/reject/reverse of a business record appends a ledger entry (actor, device, time, before/after hash). Reads of sensitive financial data are access-logged. |
| **CC-05 Entitlements** | Plan-gated features return 403 problem `billing.feature_not_in_plan` (extension `feature`, [ADR-0023](../adr/0023-plans-subscriptions-entitlements.md)) from API and show an upgrade prompt in UI. Limits behave as soft limits ([§2.4](../01-product/business-model-and-pricing.md#24-pricing-must-be-technically-enforceable--entitlements-engine)). |
| **CC-06 i18n** | All user-visible text is in FR and EN, with no hardcoded strings. Numbers, dates and units are formatted per locale. Pseudo-localization build shows no truncation. |
| **CC-07 Units** | Every quantity displays its unit (and material state for volumes). API never returns bare numbers for quantities. |
| **CC-08 Time** | Times stored in UTC, displayed in site timezone with offset available on hover. Device time and server time both kept for field records. |
| **CC-09 Offline (field features)** | The feature works with no network, the queued state is visible, it syncs exactly once after reconnection, and nothing is lost on app restart or device reboot. |
| **CC-10 Performance** | API p95 < 300 ms read / < 500 ms write on staging dataset. Field screens interactive < 100 ms after tap on reference device. |
| **CC-11 Accessibility** | Portal: WCAG 2.2 AA (keyboard, contrast, labels). Field: touch targets ≥ 48 px, outdoor high-contrast mode supported. |
| **CC-12 Errors** | Errors use RFC 9457 Problem Details with a translated user message and a correlation ID shown to the user. |
| **CC-13 Observability** | New flows emit traces with `tenant`, `module`, `operation`. Failures are logged without PII and alert when thresholds are crossed. |
| **CC-14 Idempotency** | Fact-creating POSTs accept `Idempotency-Key`; replays return the original result. |
| **CC-15 Validation** | Server-side validation mirrors client validation (shared Zod/OpenAPI schemas). Invalid data never reaches the domain. |
| **CC-16 Security** | No secrets in code or logs. Input is validated and output encoded. File uploads are type-checked, size-limited and malware-scanned when coming from portal users. |
| **CC-17 Documentation** | User-facing features have a help article stub (FR/EN) and a changelog entry. |
| **CC-18 Data export** | Any list screen of business data can be exported (CSV/Excel) subject to permissions and masking. |

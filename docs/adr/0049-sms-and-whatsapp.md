---
title: "ADR-0049: SMS and WhatsApp channels"
area: adr
tags: [adr, phase-3, p3-14, notifications, sms, whatsapp, quotas, providers]
source: "Phase 3 sub-phase plan (sub-phases.md, P3.14), US-P3-NOT-01"
status: proposed
last_updated: 2026-11-27
---

# ADR-0049: SMS and WhatsApp channels

> [Docs index](../README.md) › [ADRs](README.md) · [Phase 3 sub-phases](../04-phases/phase-03-weighbridge-fuel-pilot/sub-phases.md)

- **Status:** Proposed (2026-11-27, P3.0)
- **Date:** 2026-11-27

## Context

Critical alerts (fuel variance, suspected fraud, unsafe machine) must reach a phone without the app. Providers cost
money per message and are chosen by the owner.

## Decision

### 1. Ports with local fakes

`ISmsSender` and `IWhatsAppSender` with a local fake and an inspection page (like Mailpit) until the owner chooses
providers. Provider credentials only through the secrets store.

### 2. Quotas and metering

A monthly quota per tenant from the plan; each message metered (Billing reads it). Over quota or after failed retries →
e-mail fallback and a notice to the tenant's admins. Quiet hours per user, except critical alerts.

### 3. Delivery status

Queued, sent, delivered, failed, from provider webhooks whose signatures are verified; retries with backoff.

## Consequences

- The whole behaviour is testable now; a provider is an adapter later. Phone numbers are personal data (masking,
  export, retention).

## Alternatives considered

| Option | Why not |
|---|---|
| Wait for providers | Blocks P3.10's alerts and the demo for an external choice |

---
title: "ADR-0015: Transactional outbox for domain and integration events"
area: adr
tags: [events, outbox, messaging, consistency]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0015 — Transactional outbox for domain and integration events

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** US-P1-PLT-*, ADR-0001, ADR-0008

## Context

Modules communicate asynchronously, the ledger must be written with the business change, and notifications/read models must never be published for a change that rolled back (or lost for one that committed).

## Options considered

1. Publish to a broker after commit — **Pros:** Simple. **Cons:** Lost events on crash between commit and publish.
2. Distributed transactions (2PC) — **Pros:** Atomic. **Cons:** Complex, poorly supported.
3. **Outbox table in the same transaction + dispatcher in the Worker host** (chosen) — **Pros:** Exactly-once effect with idempotent consumers; no broker needed in MVP. **Cons:** Dispatcher lag must be monitored.

## Decision

Domain events raised by aggregates are written to an outbox table in the same PostgreSQL transaction as the business change (and ledger entry). The Worker host dispatches them in order to in-process handlers (projections, notifications, other modules) using a CloudEvents-shaped envelope. Consumers are idempotent. A message broker (RabbitMQ/Kafka/managed bus) replaces the in-process dispatcher only when a module is extracted (ADR-0001). Avoid MediatR/MassTransit v9 unless the licence is budgeted; prefer a thin in-house dispatcher or Wolverine.

## Consequences

- ✅ No lost or phantom events; broker-agnostic.
- ⚠ Outbox lag is an SLO metric with an alert and runbook.

---
title: "Runbook: KarryOutboxLagHigh"
area: operations
tags: [runbook, alert, outbox, jobs, workers]
source: "P1.4, US-P1-OPS-02 AC3"
status: baseline
last_updated: 2026-10-23
---

# KarryOutboxLagHigh

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** in one module, the **oldest domain event not yet delivered** to its subscribers is older than **5 minutes** for 2 minutes (gauge `karry_outbox_oldest_pending_age_seconds{module=…}`, severity `page`). Dead-lettered messages are not counted.
- **Impact:** everything that reacts to events is late: projections, notifications, usage meters, ledger anchoring later. Business data itself is safe: the change and its event were committed together ([ADR-0015](../../adr/0015-transactional-outbox.md)), nothing is lost, delivery resumes where it stopped.
- **Diagnosis:**
  1. **Karry · Platform** → *Outbox messages by outcome*: many `retried` = a subscriber fails; no activity at all = the worker is not dispatching.
  2. Worker running? Logs `{service_name="karry-workers"}` in Loki. Event 2003 "Outbox dispatch loop … failed; retrying" = database problem; event 2001 "failed on attempt" names the message and event type.
  3. The message blocks its aggregate: events of one aggregate are delivered strictly in order, so a failing message holds back later events of the **same aggregate** (others continue). Find it:
     ```sql
     SELECT id, type, attempts, next_attempt_at, last_error
     FROM <module>.outbox_messages
     WHERE processed_at IS NULL AND dead_lettered_at IS NULL
     ORDER BY occurred_at LIMIT 20;
     ```
  4. Traces: Tempo → `name = "outbox dispatch"` with status error shows the failing subscriber's database spans.
- **Mitigation:**
  - Worker down → restart it (it resumes safely: `FOR UPDATE SKIP LOCKED`, per-handler de-duplication).
  - Subscriber bug → fix and deploy; the message is retried with back-off (2 s doubling, max 10 min) and dead-lettered after 10 attempts. Do **not** delete outbox rows.
  - Backlog too large after an outage → run a second worker instance (dispatch is safe with several workers).
- **Resolution:** a test that reproduces the subscriber failure; for dead-lettered messages, a reviewed replay (reset `dead_lettered_at`, `attempts`, `next_attempt_at`) after the fix, recorded in the incident.
- **Escalation:** on-call engineer; technical lead (owner) if any message is dead-lettered or the lag exceeds 1 hour.

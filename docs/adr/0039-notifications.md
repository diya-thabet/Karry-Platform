---
title: "ADR-0039: Notifications"
area: adr
tags: [adr, phase-2, p2-10, notifications, email, web-push, digest, outbox]
source: "P2.10 planning (sub-phases.md §P2.10, decision table), US-P2-NOT-01, US-P2-OFF-04 AC2, US-P2-VER-06 AC2, ADR-0037, ADR-0038"
status: proposed
last_updated: 2026-11-25
---

# ADR-0039: Notifications

> [Docs index](../README.md) › [ADRs](README.md) · [P2.10 slice plan](../04-phases/phase-02-field-operations/p2.10-notifications-plan.md)

- **Status:** Proposed (2026-11-25, P2.10 planning)
- **Date:** 2026-11-25

## Context

Phase 2 now produces the events people must hear about: an entry rejected (ADR-0037), a machine held as unsafe
(ADR-0035), a review overdue and escalated, a continuity gap turned into a fraud suspicion (RULE-FO-11), a shift
whose end is missing (RULE-FO-08). Nobody is told: the operator learns of a rejection only by opening the history,
the site manager only by opening the console. The sub-phase plan reserved this ADR for the notification decisions
(numbered 0038 there; 0038 went to the controller console, so notifications take 0039 and documents 0040).

## Decision

### 1. A Notifications module fed by the outbox

A module `Notifications` (schema `notifications`, RLS) subscribes to the domain events that the other modules
already publish through the outbox (ADR-0015): it never queries their tables. A notification is one row per person:
the event's type, the words as a catalogue key with its parameters (never a pre-rendered sentence: each person reads
it in their own language), a deep link, `createdAt`, `readAt`. Rows are kept 90 days, then deleted by the Workers.

### 2. Rules: event → audience → channels, defaults in code

A rule names an event type, an audience (the producer of the record, or the holders of a role on the event's site,
or of a role across the company) and channels (in-app, e-mail digest, push). The default rules are code (reviewed
like code); a company switches a rule off, a person turns a channel off for themselves (preferences). Audiences are
resolved through Identity's contracts at delivery time: a person who left the site no longer receives its news.

| Event | Audience | Channels |
|---|---|---|
| `verification.review_item_rejected.v1` | the entry's operator | in-app (tablet), push |
| `verification.review_item_escalated.v1` | the policy's escalation role on the site | in-app, digest |
| `fieldops.asset_marked_unsafe.v1` | site managers and maintenance planners of the site | in-app, push |
| `integrity.flag_opened.v1` for RULE-FO-11 | executives and tenant administrators | in-app, digest |
| `integrity.flag_opened.v1` for RULE-FO-08 | site managers of the site | in-app, digest |

### 3. In-app: polled with `ETag`, on the tablet through the pull

The portal's notification centre polls `GET /api/v1/notifications` with `ETag` (as the review queue, ADR-0038 §3).
The field app receives the unlocked operator's notifications through the existing pull (a sync source, replace per
user): on a shared tablet, only the person unlocked sees theirs.

### 4. E-mail: an `IEmailSender` port and a daily digest

E-mail goes through an `IEmailSender` port (MailKit SMTP; Mailpit locally; the provider is chosen with the
deployment, no cloud service is needed to build or test). Only digests are e-mailed in Phase 2: one per person and
day, at 07:00 in the site's time zone, listing what the in-app centre holds for them (counts and links, no entry
values). Every send is a delivery record with its attempts; a failure is retried with backoff and never blocks the
in-app notification.

### 5. Web Push behind a flag

Web Push uses VAPID keys from configuration, one subscription per device and person, behind the feature flag
`notifications.web-push`. A push says only that something needs a look and opens the app: the words and values stay
behind the sign-in (a lock screen is public).

### 6. Words never carry what the reader may not see

Parameters are what the reader's role may read (masking rules, ADR-0022): no price, cost or wage, no other person's
personal data beyond a name already visible to them. Logs of deliveries carry IDs, never addresses or texts (T-18).

## As built

As built (slice 1): module `Notifications` (schema `notifications`, RLS; rows deletable for the 90-day retention).
`INotices.PublishAsync(connection, transaction, notice)` (Notifications.Contracts) joins the transaction of the
outbox dispatch that carries the event; `NoticeDelivery` applies the type's rule (`NoticeRules`, code), resolves the
audience through Identity's new `INoticeAudiences` (active users holding a role on the site, an assignment to that
site or to every site, or across the company) and stores one row per person, skipping those who already have the
notice of that event (unique on event and person). Producers own their subscriber on their own event: Verification
`ReviewNotices` (rejection → the entry's producer; escalation → the role the event names, on the site), FieldOps
`HoldNotices` (unsafe machine → site managers and maintenance planners of the site), Integrity `FlagNotices`
(RULE-FO-11 → executives and company administrators; RULE-FO-08 → site managers). Each takes every `INotices`
registered, so a host without the module still starts; Host and Workers register it.

Found while building: the FieldOps, Verification and Integrity outboxes were written but never dispatched (no
`AddOutboxDispatcher` with their event assemblies, no dispatcher in the Workers). They are now registered and
dispatched (and Notifications' own outbox); messages already waiting are processed by the subscribers that exist now.
The API: `GET /api/v1/notifications/mine` (newest first, `unread`, the unread count, `ETag`/304),
`POST /api/v1/notifications/{id}/read` (another's is a 404) and `POST /api/v1/notifications/read-all`, open to every
member about themselves only (reviewed list of the authorization suite).

As built (slice 2): the portal's header carries a bell, a link to `/notifications` whose name says how many notices
are unread (a badge shows the number too). The bell and the list share one query, asked again every 30 s with the
last `ETag` (a 304 keeps the list). Each notice is written in the reader's language from its type and parameters
(`noticeText`); opening one marks it read and goes to its page when the link is one of the portal's own (`/review`,
`/assets/holds`, `/assets/integrity`); a tablet's link (`/history`) is not followed in the portal, the notice is only
marked read. "Mark all read" clears the bell.

As built (slice 3): the pull gains the `notices` collection (Notifications' `NoticesSync`, replace): the notices of
the tablet's site of the types a tablet shows (a rejected entry), over the operator history's 14 days, with their
person, type and parameters. The tablet keeps them in the offline database (schema v8, `notices` by person) and shows
the unlocked person theirs on the home screen ("For you", newest first, the reason in words); "OK" marks one read in
`noticeReads`, kept on the tablet only (it pulls as a device, not as the person), so a notice the next pull brings
again stays read there. The tablet holds the site's rejections of every operator, as it already holds the site's
decided entries with their reasons (`reviewOutcomes`); it shows each person only theirs.

## Options considered

1. **Outbox subscribers in a Notifications module (chosen).** One place for rules, preferences and delivery; the
   producing modules stay unaware of who listens.
2. **Each module sends its own notices.** Fast for the first event, then every module re-implements audiences,
   preferences and retries.
3. **Server push (SSE/WebSocket) for the centre.** Unneeded for tens of users per company; polling with ETag works
   through every proxy and costs a 304.

## Consequences

- **Positive:** one audited path for every notice; the producers stay as they are; languages and masking are applied
  at reading time.
- **Negative:** a notice reaches a tablet at its next pull (minutes), not instantly, unless push is on.
- **Follow-ups:** SMS and a provider for production mail with the deployment; more events as later phases produce them.

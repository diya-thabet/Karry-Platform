---
title: "ADR-0039: Notifications"
area: adr
tags: [adr, phase-2, p2-10, notifications, email, web-push, digest, outbox]
source: "P2.10 planning (sub-phases.md §P2.10, decision table), US-P2-NOT-01, US-P2-OFF-04 AC2, US-P2-VER-06 AC2, ADR-0037, ADR-0038"
status: accepted
last_updated: 2026-11-26
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

As built (slice 4, API): each rule names its channels (§2 table) and the only parameters its notices may carry
(`NoticeRules`): identifiers and codes (letters, digits, `_ . : , + -`, at most 200 characters). `NoticeDelivery`
drops anything else before storing, whatever the producer sent: a price, a reading, a note or a name never reaches a
notice (§6, TC-P2-NOT-004). A company switches a rule off or on with `PUT /notifications/rules/{noticeType}`
(`notifications.rules.manage`, tenant administrator, privileged): off silences an alert for everyone, so it needs a
recent sign-in; on does not. Each switch is a `notifications.rule_switched.v1` ledger entry with who made it; a type
without a row is on; notices already delivered stay. A person reads and sets their own channel choices with
`GET /notifications/preferences` and `PUT /notifications/preferences/{noticeType}/{channel}` (any member, their own
only): only the optional channels a rule uses (digest, push) can be turned off; the in-app centre cannot (it is where
every notice is kept). Preferences are personal, not business facts: not ledgered. They are honoured by the digest and
push slices.

As built (slice 4, portal): the notification centre ends with "How you are told": for each kind of notice, the
optional channels its rule uses (daily e-mail, push), each saved as soon as it is changed; the in-app list is never
offered; a kind the company switched off says so and keeps the person's choices. Settings › Notification rules (only
with `notifications.rules.manage`) lists each kind with who hears of it and by which channels, and a switch: off asks
for the password again (step-up, then the call is retried), on does not; the card says when and by whom it last
changed. Named "Notification rules", not "Notifications", which is the header bell's name.

As built (slice 5a): the port `IEmailSender` (BuildingBlocks `Messaging`: `EmailMessage`, an `EmailOutcome` of sent,
not configured or failed; it never throws for a delivery failure). Its SMTP adapter is Identity's `SmtpEmailSender`,
on the same `Identity:Messaging` server as the invitations (Mailpit locally): Identity owns the sender settings, and
keeping MailKit there spares every other project the dependency. Notifications keeps one `email_deliveries` record per
e-mail (RLS): the person (an Identity user), never the address, which `INoticeAudiences.ContactOfAsync` gives at each
attempt (active users with an address only); a unique key per company so an e-mail is queued once; the attempts, the
next attempt and the last error as a code (`smtp_failed`, `not_configured`, `no_address`). A failure is tried again
after 1, 5, 15, 60 and 240 minutes and given up after the sixth attempt; a person without an address at once. The
Workers' `EmailDeliveryJob` sends the due ones every minute, company by company, saving each outcome before the next
send. Logs carry neither address nor text (T-18).

As built (slice 5b): `DailyDigests` queues the digests of the current company; the Workers' `DigestJob` runs it every
15 minutes, company by company. A site's digest is due from 07:00 to noon in the site's time zone (NodaTime, as
Organization; an unknown zone is read as UTC; the noon limit catches up after downtime without mailing in the
afternoon), and covers yesterday 07:00 to today 07:00 local, so a clock change is a 23- or 25-hour day. It goes to the
site's managers and to everyone with a digest notice about the site in that period (the types whose rule uses the
digest channel and is on). A manager's digest carries the site's review counts (Verification's new `IReviewCounts`:
waiting now, of which overdue, corrections since yesterday 07:00; US-P2-VER-06 AC2); they follow the choices of
`review.escalated` (the company's switch and the person's digest preference). A person's notices are counted by kind,
without the kinds they turned off. Nothing to say, nothing sent. One e-mail per person, site and day (the delivery key
`digest:{person}:{site}:{day}`), in French then English (the server does not know the reader's language, as for
invitations), with links to the portal's `/review` and `/notifications` (Identity's new `IAppAddresses`, the
invitations' address). Counts and links only: no entry, reading or name. The e-mail job of 5a sends it.

As built (slice 6a, server): push is on for a company when the VAPID keys are configured (`Notifications:WebPush`:
`PublicKey`, the 65-byte P-256 point, and `PrivateKey`, its 32-byte scalar, base64url; the private key from the vault)
and its flag `notifications.web-push` is on (default off). A push is an empty `POST` signed with VAPID (RFC 8292: an
ES256 JWT for the push service's origin, 12 hours, the contact `Subject`): no payload, so no RFC 8291 encryption, and
nothing on a lock screen. Subscriptions (`push_subscriptions`, RLS): a person's browser (`PUT /notifications/push/
subscription`, any member, their own) or a tablet for its site (`PUT /notifications/push/device/subscription`, the
device). The tablet subscribes as itself, not per person: it pulls as a device and holds the site's rejections, and the
push says nothing, so per device is enough and needs no PIN. An endpoint must be an HTTPS address, on the default port,
of the browsers' push services (FCM, Mozilla, Windows, Apple); redirects are not followed: a client cannot make the
server call elsewhere (SSRF). The endpoint is a capability, never logged nor put in a URL (removal is a `POST` with a
body). A stored notice queues pings in its own transaction (`push_pings`, RLS), only for the people reached now: a
tablet notice (a rejection) pings the site's tablets unless its operator turned push off for it; another notice pings
the browsers of its recipients who kept push. One ping waits per subscription at most; the Workers' `PushDeliveryJob`
sends them every minute, retries after 1, 5 and 15 minutes, gives up after four attempts, and deletes a subscription the
push service says is gone (404, 410).

As built (slice 6b, apps): the field app offers "Alertes" on the home screen while the device key endpoint answers
(push on for the company) and the browser can; a tap asks the browser's permission and subscribes the tablet as itself
(signed device request); a tablet already allowed is resubscribed silently each time the home screen shows, so the
server always has its current endpoint. Its Workbox service worker loads `push-sw.js` (`importScripts`), which shows
"Quelque chose demande votre attention." (English when the browser is English) and opens or brings the app forward on
a tap. The portal shows "Alerts in this browser" on `/notifications` with a button to turn them on or off; it registers
its own worker (`/push-sw.js`, push only, no cache) when the person turns them on, and a tap opens `/notifications`.
Neither alert says what happened: the person reads it behind the sign-in (the lock screen is public).

## Security review (P2.10 close-out, 2026-11-26)

| Threat | What P2.10 does | Evidence |
|---|---|---|
| T-09 cross-tenant and cross-scope delivery | Audiences are resolved at delivery from Identity in the event's company (RLS): a role on a site, an assignment to that site or to every site, or the company; a person who left no longer hears. Every Notifications table has RLS (notifications, rule settings, preferences, e-mail deliveries, push subscriptions and pings). A person reads and marks only their own notices, sets only their own choices and subscriptions; a tablet subscribes only for its own site and receives only its site's rejections | TC-P2-NOT-002 (`NotificationsTests`), the isolation suite (`notificationId`, `noticeType`, `channel`), `A_rejection_alerts_the_sites_tablets…` |
| T-10 masking in messages | A notice stores only the parameters its rule names, and only identifiers and codes (letters, digits, `_ . : , + -`, 200 characters): a price, a reading, a note or a name sent by a producer is dropped before storing. Words are written by the reader's app; the digest is counts and links; a push carries nothing | TC-P2-NOT-004 (`A_notice_keeps_only_the_identifiers_and_codes…`), TC-P2-NOT-003 (digest text), `VapidPushSenderTests` (empty body) |
| T-18 personal data in delivery logs | Deliveries keep the person's ID, never the address (looked up at each attempt); errors are codes (`smtp_failed`, `no_address`, `push_failed`); the SMTP and push logs name the failure and the status code only, never an address, a text or an endpoint | `NotificationsTests.Email`, `SmtpEmailSender`, `VapidPushSender` log messages |
| SSRF through push endpoints | A subscription endpoint comes from a client, so the server only calls HTTPS addresses on the default port of the browsers' push services (FCM, Mozilla, Windows, Apple), without credentials in the URL, and follows no redirect; the endpoint is never put in a URL or a log | `Only_a_browsers_push_service_over_https_is_an_endpoint` (look-alike hosts, other ports, credentials, the metadata address refused) |
| Silencing alerts | Switching a rule off is privileged (Tenant Admin, second factor), needs a recent sign-in and is ledgered with who did it; switching it on needs no step-up | `A_company_switches_a_rule_off…` |
| Lock screens | A Web Push and its alert say only that something needs a look; the content stays behind the sign-in (portal) or the unlock (tablet) | `push-sw.js` of both apps |
| Residual | The VAPID private key is a deployment secret (vault); without it push stays off. E-mail goes through the platform's SMTP relay, chosen with the deployment (P1.17); until then Mailpit locally and nothing in production | operations |

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

---
title: "ADR-0038: Controller console"
area: adr
tags: [adr, phase-2, p2-9, verification, portal, review-queue, evidence, keyboard, shift-board]
source: "P2.9 planning (sub-phases.md §P2.9), US-P2-VER-01, 07, 08, ADR-0032, ADR-0036, ADR-0037"
status: proposed
last_updated: 2026-11-24
---

# ADR-0038: Controller console

> [Docs index](../README.md) › [ADRs](README.md) · [P2.9 slice plan](../04-phases/phase-02-field-operations/p2.9-controller-console-plan.md)

- **Status:** Proposed (2026-11-24, P2.9 planning)
- **Date:** 2026-11-24

## Context

P2.8 built the decisions (ADR-0037): review items, approve, reject, correct, second approval, bulk approval,
policies, escalation, and settling continuity gaps. All of it is reachable only through the API. A controller now
needs a screen that gets through a shift fast: 50 unflagged entries in under 5 minutes (US-P2-VER-07 AC3), the
risky ones first, the meter photos side by side, and the keyboard rather than the mouse.

Three things are missing underneath. The queue sorts by creation, not by risk, and filters only by site and status.
Nothing serves an entry's photos to the portal: evidence blobs go from the tablet straight to the store by a
pre-signed `PUT` (ADR-0032) and are only read back by `HEAD`. And "missing end" (RULE-FO-08), which the shift board
shows, is not a rule yet.

## Decision

### 1. The queue sorts by risk on the server

`GET /api/v1/verification/items` gains `sort=risk`: overdue first, then flagged, then oldest first; the cursor
becomes the sort key and the ID, so paging stays stable while new items arrive. It also filters by asset, operator
(`producedBy`), flag and the covered date. The portal never sorts a page on its own: a client sort of one page would
hide a risky item on the next one.

### 2. Photos are streamed by the API, checked on every request

`GET /api/v1/fieldops/entries/{entryId}/photos/{sha256}` answers only for a photo that a version of that entry names
(its start or end photo), for a caller with `fieldops.entries.read` on the entry's site. The API reads the blob from
the evidence bucket by its server-built key (the envelope's tablet and the hash) and streams it with the headers of
the Assets meter photos: `private, max-age=86400, immutable`, `default-src 'none'`, `nosniff`. `IEvidenceStore`
gains a read. The portal fetches it with the person's token and shows it from a `blob:` URL, as it does asset
photos. No pre-signed `GET` URL leaves the server: a URL in a browser's history or a screenshot would open the photo
for anyone for its lifetime, and the portal's content security policy keeps `img-src 'self' data: blob:` without
the store's host.

### 3. The console refreshes by polling with `ETag`

The queue asks again every 5 seconds while the page is visible, with `If-None-Match`; an unchanged page is a 304
with no body (AC4: a new synced entry shows within 10 s). Server push (SSE or WebSocket) is P2.10's notification
channel and is not needed for a 10 s target.

### 4. Keyboard first, never while typing

A approve, R reject, C correct, J/K next and previous, ? the help overlay. Shortcuts act only on the focused item
and never fire inside a text field or an open dialog. A decision that needs a recent sign-in (correct, second
approval, settle) opens the step-up first and then carries on; a 409 shows the item as it now is. Tablets get swipe
actions for approve and reject only.

### 5. "Missing end" is an Integrity rule run by the Workers

RULE-FO-08: a segment with no end one hour after its shift ended raises a flag on the entry (high), closed when the
end arrives. The Workers run it every 5 minutes per company, like the SLA job. The shift board reads planned shifts,
entries and open flags; it adds no table of its own.

## Options considered

1. **Pre-signed `GET` URLs for photos.** Less load on the API, but a bearer URL outlives the check and widens the
   content security policy to the store's host. Rejected for evidence; reconsider for large exports.
2. **Sorting the queue in the browser.** Simple, wrong across pages. Rejected.
3. **Server push for the queue now.** More moving parts than a 10 s target needs; P2.10 brings the channel.

## Consequences

- **Positive:** a controller works one queue in risk order with every fact on one screen; photos are as protected
  as the entries they belong to.
- **Negative:** photo bytes go through the API (about 200 KB each, cached privately by the browser for a day).
- **Follow-ups:** P2.10 notifications reuse the board's "missing end"; P2.11 prints decisions on the shift report.

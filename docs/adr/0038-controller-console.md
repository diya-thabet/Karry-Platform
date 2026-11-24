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

## As built

As built (slice 1): review items carry `risk` (0 none, 1 flagged at submission, 2 escalated), kept by the domain and
backfilled by the migration, indexed with the status and ID. `GET /verification/items?sort=risk` orders by risk then
ID (UUIDv7, so oldest first); the cursor stays the last item's ID and the server reads that item's current risk to
place the next page, so the API's cursor did not change type (no breaking change). Filters: `assetId`, `producedBy`,
`flag` (jsonb element, letters, digits, `_` and `-` only), `coveredFrom` (inclusive), `coveredBefore` (exclusive).
The page's `ETag` is a hash of each item's ID and row version and of the next cursor; `If-None-Match` with it is a 304
without a body; the answer is `private, no-cache`. An item escalated while someone pages moves up and may be seen
twice or not at all on that pass: the next poll from the first page shows it.

As built (slice 2): `IEvidenceStore.GetAsync` reads an object (the S3 store with the same retries as the object
store). `GET /api/v1/fieldops/entries/{entryId}/photos/{sha256}` (with or without the `sha256:` prefix, lower-case
hex only) loads the entry for a caller with `fieldops.entries.read` on its site (else 404), looks for the first
version that names the photo as its start or end and was sent by a tablet (a correction names none), and reads the
blob under that tablet's key: the end may come from another tablet than the start. A photo of the same company that
the entry does not name is a 404, never served. No store configured: 422 `fieldops.photos_unavailable`, as for asset
photos. FieldOps registers the evidence store itself (`TryAdd`, the same as Sync's), so a host without Sync still
starts. `GET /fieldops/entries/{id}` gains `previousOnMeter`: the entry that started last before it on the same
meter (any version: a correction never changes a start time), as it stands, only when the caller sees its site.

As built (slice 3): the sidebar's Operations group gains **Review** (`/review`). The page asks the queue in risk
order (50 items) and, when the person may read entries, those entries in one request
(`GET /fieldops/entries?ids=…`, up to 200 IDs; no IDs is no filter), never one request per row. Each row: the
machine's code, the shift (date and code) and site; start → end with the unit and Δ; the start and end photos
(fetched with the token, shown from `blob:` URLs, kept for the session); the entry's flags in words; overdue or due
time, and "waiting for a second approval". Filters: site, flag, overdue only. The query asks again every 5 s while
the tab is visible (TanStack Query stops in the background) with the last page's `ETag`; a 304 keeps the rows.
The API client throws on every non-2xx, so the queue's own fetch treats a 304 as "unchanged" before it reaches the
error dialog. Machine codes come from the company's first 200 machines (a larger fleet shows a dash beyond them
until the detail view of slice 4 reads the machine itself).

As built (slice 4): each queue row opens a panel (`ReviewDrawer`). It shows the previous end on the same meter
(`previousOnMeter` of the entry's detail) beside this entry's start and end, each photo at 160 px with its value,
Δ and the fuel levels, the flags in words, the signature's verification badge (for people who may verify), and the
versions when there are several. Decisions send the item's version as `If-Match`: approve; reject with a reason code
and 10–500 characters (the operator reads them on the tablet); correct with the values that changed only (a meter
value with at most one decimal, a fuel level 0–100, as typed with a comma or a dot) and a reason, behind the
recent-sign-in prompt, saying when the correction waits for another person; the second approval of a correction,
for whom may give it, behind the same prompt. A 409 (someone decided meanwhile) closes the panel, says so and
refreshes the queue, without the error dialog; the other refusals (separation of duties, a value the entry cannot
take) show their words from `errors.verification.*`. Zooming a photo is not built: 160 px with the value beside it
reads a meter; the usability test (slice 8) decides whether a zoom is needed.

As built (slice 5): the keys are read by one pure function (`shortcutOf`): A, R, C, J, K and ? in either case;
nothing with Ctrl, Alt or Cmd, nothing from a text field, a list or an editable area, nothing while a dialog or the
review panel is open (TC-P2-VER-011). J and K move a highlighted row (the table's selected row) and stop at the ends
of the page; the browser's focus follows, so Enter opens it and a screen reader reads it. A approves the highlighted
entry when it is pending (flagged too: one by one is allowed); R and C open its panel on the rejection or the
correction. Entries without a flag carry a checkbox; "Choose every entry without a flag" and "Approve N entries"
send one bulk approval (all or nothing, each at its version). On a touch screen, `DataTable.onSwipe` reports a
mostly horizontal swipe of 80 px or more toward the end or the start of the reading direction (mirrored in Arabic and
N'Ko): toward the end approves an entry without a flag, toward the start opens it. Swipe never approves a flagged
entry, so a stray gesture cannot pass one that needs a look.

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

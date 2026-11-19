---
title: "ADR-0035: Pre-start checklists and safety holds"
area: adr
tags: [adr, phase-2, p2-6, fieldops, checklists, safety, offline, signatures, sync]
source: "P2.6 planning (sub-phases.md §P2.6), US-P2-FO-02, US-P2-FO-03, ADR-0031, ADR-0033, ADR-0034"
status: proposed
last_updated: 2026-11-19
---

# ADR-0035: Pre-start checklists and safety holds

> [Docs index](../README.md) › [ADRs](README.md) · [P2.6 slice plan](../04-phases/phase-02-field-operations/p2.6-checklists-plan.md)

## Context

Before a machine starts, its operator goes through a checklist (US-P2-FO-02): OK / Not OK / N/A per item, a photo on a
critical Not OK. A critical Not OK makes the machine **unsafe**: nobody may start a shift on it until a manager
overrides, with a reason, audited. Site managers build the checklists per asset type, in versions (US-P2-FO-03); a
submission keeps the version it answered. The tablet works offline (ADR-0028), so a checklist is a signed fact
uploaded later (ADR-0031, ADR-0033), and the server never refuses a signed fact over a business rule (ADR-0034).

## Decision

### 1. FieldOps owns templates, submissions and holds

Checklist templates, their submissions and the **safety holds** they raise live in the FieldOps module, next to the
shift entries they gate. Assets is not changed: a hold is a fact about how a machine may be used, decided by signed
field facts and manager decisions, not an attribute of the asset register. The portal and the tablet read holds from
FieldOps (API and sync). The event `fieldops.asset_marked_unsafe.v1` (P2.10 notifications) and
`fieldops.asset_released.v1` go through the FieldOps outbox.

### 2. Templates are versioned per asset type

A template belongs to one asset type (`excavator`, …), has a `required` switch (US-P2-FO-02 AC1: required or optional
before a start) and items `{code, texts: {language: text}, critical, photoOnNotOk}`. There is no server-side draft: the portal edits
locally and **each publish** sends the full content, saved as the next version (1, 2, …), never changed afterwards
(append-only table, `UPDATE`/`DELETE` refused); the newest published version is the one tablets use. Two publishes at
once: one wins, the other gets 409 `fieldops.checklist_version_conflict`. A critical item always asks for a photo on
Not OK. Reading needs `fieldops.checklists.read`, publishing `fieldops.checklists.manage` (company-wide permissions). Texts are kept in the company's languages (fr, en, ar, emk, nqo as chosen); the tablet shows the
operator's language, else the first one.

### 3. A submission is a signed record of its own

`fieldops.checklist` records, signed with the same envelope as shift entries (ADR-0033): `{rootId, version: 1,
templateId, templateVersion, assetId, submittedAt, answers: [{item, answer: ok | not_ok | na, photo?}]}`; the photos
are evidence blobs (ADR-0032). The command `fieldops.checklist.submit.v1` carries `{record, envelope, deviceSignature,
evidence, captures}`. The server checks the shape and the answers against the template version named (every item
answered once; a photo where the version asks for one); a submission naming a version the server does not know is
refused (`fieldops.checklist_invalid`), a version older than the newest is accepted as it is (TC-P2-FO-013).

### 4. A critical Not OK opens a hold, in the same transaction

Storing a submission with a critical Not OK opens a hold on the asset (`fieldops.safety_holds`, append-only events:
opened by a submission, released by a decision) and raises `asset_marked_unsafe`. While a hold is open:

- **the tablet** refuses to start a shift on the machine (it knows the hold from the pull and from its own unsent
  submissions), with the reason;
- **the server** still stores a start signed on it (it may have been signed before the hold arrived) and flags it
  `ASSET_UNSAFE` for the controller (ADR-0034's rule: flag, never refuse).

### 5. Releasing a hold is a manager decision, ledgered

`POST /api/v1/fieldops/holds/{holdId}/release {reason}` with the permission `fieldops.holds.release` (site scope;
Site Manager and Maintenance Manager by default): the decision, its author and reason go to the ledger with the
release event, in one transaction; the reason is required (10–500 characters). "Signed" in the sub-phase card means
this: the portal user is authenticated, the decision is ledgered under their name (the portal has no device key).
Without the permission: `403` (TC-P2-FO-014).

### 6. Sync

Two collections join the tablet's pull: `checklistTemplates` (the newest published version per asset type of the
site's machines, replace-whole) and `safetyHolds` (open holds of the site's machines, replace-whole).

## Options considered

1. **FieldOps owns holds (chosen).** One module for the signed facts and what they decide; Assets stays a register.
2. **An `Unsafe` asset status in Assets, set from a FieldOps event.** Mixes a maintenance status with a safety
   decision, needs a cross-module write for every submission, and the status loses the history of who decided.
3. **Refuse a start on an unsafe machine on the server.** Breaks "a signed fact is never lost" (a start signed before
   the hold reached the tablet would vanish). Rejected: flag instead.
4. **A portal signature with a device-like key for overrides.** No key custody for portal users exists; the ledger
   already binds the decision to its author. Revisit with qualified signatures (Phase 10).

## Consequences

- **Positive:** the checklist answer and the hold it opens are one signed, ledgered chain; templates can change
  without rewriting history; the field never loses a fact.
- **Negative:** a start signed offline on a machine held meanwhile reaches the server flagged, not prevented (the
  controller sees it); templates in five languages need someone to write them.
- **Follow-ups:** P2.10 notifies the site manager and maintenance on `asset_marked_unsafe`; P4 (maintenance) may
  release a hold when a work order closes.

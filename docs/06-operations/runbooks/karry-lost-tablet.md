---
title: "Runbook: a field tablet is lost or stolen"
area: operations
tags: [runbook, security, tablets, field, offline, revocation, T-17]
source: "P2.5 security review (ADR-0034 §10), ADR-0028, threat T-17"
status: baseline
last_updated: 2026-11-18
---

# A field tablet is lost or stolen

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

A field tablet keeps its site's reference data, its signed entries waiting to be sent, their photos until 7 days after
they are confirmed, and the drafts of forms left unsigned (dropped 24 hours after their last change). Its device key
cannot be copied out of the browser, and the operators' PINs are stored only as verifiers
([ADR-0028](../../adr/0028-field-devices-offline-data-shared-tablet.md),
[ADR-0034 §10](../../adr/0034-shift-entries.md#10-security-review-p25-tablet-flows-2026-11-18)). This is a procedure,
not an alert: the site reports it.

## Steps

1. **Revoke the tablet at once.** Portal › Settings › Site tablets › the tablet › Revoke (site manager or company
   administrator). From then on its requests are refused (`401`) and any upload it still makes is quarantined, not
   recorded (TC-P1-OFF-005).
2. **Find what it had not sent.** Its entries that never reached the server are lost with it: compare the shift's
   paper backup or the operators' account with the portal for the hours since its last upload (the tablet's list shows
   its last contact). Missing entries are recorded again on another tablet; the controller notes why.
3. **Change the PINs of the operators who used it** if the tablet may be in someone else's hands: Portal › Users ›
   the operator › Set PIN. A PIN verifier kept on the tablet can be attacked offline (PBKDF2, ADR-0028); a new PIN
   makes that useless.
4. **If the tablet is found:** do not unlock it for work. Clear the browser's site data for the field address
   (Chrome › Site settings › the address › Delete data), which removes its key, entries, photos and drafts; then enrol
   it again with a new code as a new tablet.

## Why there is no remote wipe

The field app is a web app: the server cannot erase a browser's storage. Revocation makes the tablet useless to the
company at once, and the data it holds is limited: one site's reference data, at most 7 days of confirmed photos, and
drafts of the last 24 hours.

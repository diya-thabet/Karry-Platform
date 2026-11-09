---
title: "Runbook: KarrySyncGapOpen"
area: operations
tags: [runbook, alert, sync, tablets, field, workers]
source: "P2.1 slice 2b, ADR-0031 §4, US-P2-OFF-03"
status: baseline
last_updated: 2026-11-09
---

# KarrySyncGapOpen

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** a tablet's commands have waited for an earlier sequence number for over **24 hours**, for 15 minutes (`karry_sync_oldest_waiting_age_seconds` above 86 400; severity `ticket`). The Workers job measures every 5 minutes.
- **Impact:** the server holds the tablet's later commands (`sync.waiting_commands`) but cannot handle them until the missing ones arrive ([ADR-0031 §4](../../adr/0031-sync-command-processing.md#4-gaps-in-the-sequence-held-then-handled-in-order)). Those entries do not appear in the controller's review, and the missing commands may be **lost on the tablet** (wiped, broken, browser data cleared, a backup restored). Other tablets are not affected.
- **Diagnosis:**
  1. Worker logs, event **7206** (`Tablet {DeviceId} of tenant {Tenant} has {Waiting} command(s) waiting for an earlier number for {Hours} h`): it names the tablet and its company. Event **7207** means a company could not be measured (database error; the others are still measured).
  2. Run it by hand: `Karry.Workers measure-sync-gaps` prints the waiting commands and the number of tablets over 24 h (exit 1 if there is one).
  3. Look at the tablet's sequence (as the database owner, read-only):
     `SELECT max(sequence) FROM sync.inbox WHERE device_id = '<device>' AND status = 'Received';` and
     `SELECT sequence, type, received_at FROM sync.waiting_commands w WHERE device_id = '<device>' AND NOT EXISTS (SELECT 1 FROM sync.inbox i WHERE i.device_id = w.device_id AND i.id = w.id) ORDER BY sequence;`.
     The numbers between the two are the missing ones.
  4. In the portal, **Devices** shows the tablet's site, its last contact and whether it was revoked or re-enrolled.
- **Mitigation:**
  - The tablet is still in use: ask the site to open the field app online. It sends the missing numbers itself (each upload answer lists them in `missingSequences`), and the waiting commands are handled in the same request.
  - The tablet was wiped, lost or re-enrolled: the missing commands are gone. **Never delete or edit waiting commands** (append-only, and they are signed evidence). Tell the company's Tenant Admin which tablet, which site and from what time, so that the missing entries can be written down again. Recording a decision to close the gap for good (a "lost commands" record) comes with the controller console (P2.9); until then the alert stays and is acknowledged in the ticket.
- **Resolution:** the missing commands arrive, the waiting ones are handled, the age drops to 0 and the alert clears.
- **Escalation:** ticket for the platform engineer within the working day; tell the company the same day if data is lost.

---
title: "Runbook: evidence upload failing (KarryEvidenceStoreUnavailable, KarrySyncBlobMismatch)"
area: operations
tags: [runbook, alert, sync, evidence, object-storage, security, tablets, field]
source: "P2.2 close-out, ADR-0032, US-P2-OFF-01, threat T-03"
status: baseline
last_updated: 2026-11-09
---

# Evidence upload failing

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

Tablets upload each photo straight to the evidence bucket with a pre-signed `PUT`, then send the signed command that
names it; the API checks the stored object (size and SHA-256) before handling the command
([ADR-0032](../../adr/0032-evidence-blobs.md)). The counter `karry_sync_blobs_total{result}` counts each step:
`presigned`, `exists` (already stored), then `verified`, `missing`, `mismatch` or `unavailable`.

## KarryEvidenceStoreUnavailable (ticket)

- **Symptom:** the API could not reach the evidence store while checking commands for 15 minutes
  (`rate(karry_sync_blobs_total{result="unavailable"}[5m]) > 0`). The presign endpoint answers `503` or
  `422 sync.blobs_unavailable`.
- **Impact:** commands with photos are refused with the retryable code `sync.blobs_unavailable`: they stay in the
  tablets' outboxes and are sent again (every 2 minutes, then with backoff up to 30). **Nothing is lost**, but those
  entries are not recorded or reviewed until the store answers. Commands without evidence are not affected.
- **Diagnosis:**
  1. Is the object store up? Locally: `docker compose -f deploy/docker/compose.yaml ps s3`; check its logs.
  2. API settings `Storage:Evidence` (`Bucket`, `PublicUrl`) and `Storage:ServiceUrl` and credentials: a wrong
     bucket or key gives `unavailable` on every check. With no evidence store configured at all, the API answers
     `422 sync.blobs_unavailable` from the start (by design: a deployment without one cannot take photos).
  3. API logs around the time: `ObjectStoreUnavailableException`, timeouts to the store.
- **Mitigation:** bring the store back, or fix the settings and restart the API. Do not ask sites to delete
  anything: the tablets send their outboxes again on their own.
- **Resolution:** checks give `verified` again, the rate of `unavailable` drops to 0 and the alert clears.
- **Escalation:** ticket for the platform engineer within the working day; page if it lasts over 2 hours on a
  working site (entries pile up on the tablets).

## Uploads fail but the store is up (no alert)

- `missing` rises: tablets send commands before their photo is stored. The command is refused with the retryable
  `sync.blob_missing` and the tablet uploads the photo again. A steady rate from one site usually means the `PUT`
  is blocked: check CORS (`Storage:Evidence:AllowedOrigins` must list the field app's origin; the bucket's CORS
  rule is written when it is created) and that `PublicUrl` is reachable from the site's network.
- `presigned` without `verified`: the browser's `PUT` fails. `403 SignatureDoesNotMatch` means a header was changed
  on the way (a proxy rewriting `Content-Type`); `400 BadDigest` means the bytes differ from the hash; `403` after
  15 minutes means the URL expired (the tablet asks for a new one on its next run).

## KarrySyncBlobMismatch (page, security)

- **Symptom:** the stored object under a photo's key does not have the size or SHA-256 the signed command names.
  Fires at once on any mismatch and stays 15 minutes.
- **Impact:** the command was refused for good (`sync.blob_mismatch`) and kept in its place in the tablet's sequence
  ([ADR-0031 §3](../../adr/0031-sync-command-processing.md)). The operator sees "The photo received is not the one
  that was taken. Tell a manager." A mismatch should never happen: the store refuses a `PUT` whose bytes do
  not match the signed checksum, and Object Lock keeps every version. Treat it as possible tampering (threat T-03).
- **Diagnosis:**
  1. API logs, event **7208** (`Security: command {CommandId} of tablet {DeviceId} names blob {Sha256}, but the store
     holds other content under its key`) and event **7209** for the kept rejection.
  2. In the store, list the versions of `tenants/{tenant}/devices/{device}/evidence/{sha256}`: Object Lock keeps each
     one, so the first version and who wrote it can be compared.
  3. In the portal, **Devices**: the tablet's site, last contact, enrolment history.
- **Mitigation:** **never delete or overwrite** the object or its versions (retention in compliance mode, and they are
  evidence). If the tablet is suspect, revoke it in **Devices**. Tell the company's Tenant Admin which entry was
  refused; the photo is still on the tablet (refused commands keep their evidence) for a manual check.
- **Escalation:** security incident: the on-call engineer looks within the hour and opens an incident record.

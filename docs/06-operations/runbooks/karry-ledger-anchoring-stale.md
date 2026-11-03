---
title: "Runbook: KarryLedgerAnchoringStale"
area: operations
tags: [runbook, alert, audit, ledger, anchoring, worm]
source: "P1.6, US-P1-AUD-02 AC2"
status: baseline
last_updated: 2026-10-23
---

# KarryLedgerAnchoringStale

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** no **fully successful** anchoring run for **2 hours** (gauge `karry_ledger_anchor_last_success_seconds`), or the worker has not reported anchoring at all for 10 minutes (severity `page`). The job runs hourly at :05 and at worker start-up.
- **Impact:** no business impact; the ledger keeps being written in every transaction. What weakens is **provability**. Entries after the last anchor are protected only by the hash chain in the database, so a consistent rewrite of those entries by someone with full database access would not be caught until the next anchor. Restore anchoring within hours.
- **Diagnosis:**
  1. Worker running? Loki `{service_name="karry-workers"}`. Event **4001** "Ledger anchoring failed for tenant …" names the tenant and carries the exception.
  2. Storage: WORM bucket reachable, credentials valid, bucket created **with Object Lock** (ADR-0017). Typical errors: `AccessDenied`, `NoSuchBucket`, an `InvalidRequest` about missing checksums or Object Lock.
  3. Signing key: the worker refuses to start if `Trust:Anchoring` has a bucket but no key (local: `~/.karry/anchor-signing-dev.pem`; environments: KMS, P1.17).
  4. Database: `audit.ledger_heads` and `audit.anchors` readable by the worker (RLS per tenant; the job sets each tenant in turn).
- **Mitigation:** fix the cause, then run one catch-up immediately: `dotnet Karry.Workers.dll anchor-now`. Exit 0 = every tenant anchored; 1 = some failed, and the log names them. One run anchors each tenant's **current** head, which covers every entry written during the outage (TC-P1-AUD-004). Safe beside the running worker: runs take turns on a database lock, so an anchor is never written twice (H-C5); a manual run that seems to hang is waiting for the service's run to finish.
- **Resolution:** check that integrity verification passes with anchors for an affected tenant (`karry-ledger verify --tenant <id>`, or `POST /api/v1/audit/verify` → `anchorsChecked` > 0). Record the outage window in the incident: entries written then were anchored late.
- **Escalation:** on-call engineer; technical lead (owner) after 4 hours, or immediately if verification reports a mismatch between the chain and an anchor (possible tampering: treat as a security incident, preserve the database and the bucket).

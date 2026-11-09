---
title: "Offline-First & Synchronization"
area: architecture
tags: [offline, pwa, sync, dexie, devices]
source: "MASTER_PLAN.md §12"
status: baseline
last_updated: 2026-10-21
---

# Offline-First & Synchronization

> [Docs index](../README.md) › [02-architecture](README.md)

## 12.1 Client data layers

```
Dexie (IndexedDB)
├── reference/*       master data snapshot for the user's sites (assets, materials, customers, roles)
├── drafts/*          unsaved form state (auto-saved)
├── outbox            signed, immutable commands in sequence: pending → waiting / confirmed / rejected, with the server's code and flags (P2.2)
├── blobs             photos/signature strokes (compressed WebP/JPEG ≤ 1 MB, one pre-signed PUT each; ADR-0032)
└── sync_state        per-collection cursors, last success, device clock offset
```

## 12.2 Sync protocol

1. **Push:** `POST /api/v1/sync/batches` with up to 100 commands, each signed by the tablet's key; its `commandId` (UUIDv7) makes a resend harmless (no `Idempotency-Key`). The server answers per command `accepted | conflict(flags) | duplicate | waiting | rejected(code, retryable)` (P1.15, [ADR-0028 §6](../adr/0028-field-devices-offline-data-shared-tablet.md#6-uploads-preparing-phase-2); handlers run in the upload since P2.1, [ADR-0031](../adr/0031-sync-command-processing.md)).
2. **Blobs:** uploaded first. `POST /api/v1/sync/blobs/presign {sha256, size, contentType}` answers `exists` or a pre-signed `PUT` (15 min) signed with `x-amz-checksum-sha256`, so the store refuses other bytes; commands reference blob hashes and the server checks the object when it handles them ([ADR-0032](../adr/0032-evidence-blobs.md)).
3. **Pull:** `GET /api/v1/sync/changes?cursor=…` returns the reference data of the tablet's site as a snapshot or a delta ([ADR-0028 §5](../adr/0028-field-devices-offline-data-shared-tablet.md#5-reference-data-and-delta-pulls-us-p1-off-03)); since P2.1 it also holds the `submissions` collection, the final status of each command the tablet sent.
4. **Ordering:** per-device sequence without gaps; commands after a gap are held and answered `waiting` with `missingSequences`, then handled in order when the gap closes; a reused number is rejected ([ADR-0031 §4](../adr/0031-sync-command-processing.md)).

## 12.3 Conflict policy

| Data type | Policy |
|---|---|
| Field facts (readings, fuel, tickets) | **No conflicts by design**: they are immutable facts. Duplicates deduplicated by ID. Business conflicts (e.g., continuity mismatch) become **exceptions** for review, not overwrites. |
| Master data edited offline (rare; restricted roles) | Server wins; client shows a diff and asks the user to reapply. |
| Drafts | Local only. |

## 12.4 Time integrity

- Each command stores **device time** and **server received time**; the client measures clock offset at every successful sync.
- Entries with device/server skew beyond tolerance (e.g., > 10 min unexplained) are tagged for controller attention — a common fraud vector (backdating).

## 12.5 Device management

- Devices are **enrolled** (QR code by admin) → device key pair generated (WebCrypto, non-extractable) → public key registered → device bound to tenant/site.
- Lost device: admin revokes; outstanding unsynced data from revoked device is quarantined for review.
- Shared tablets: employee ID + PIN unlock, checked online first and offline against per-tablet PIN verifiers for 72 h after the user's last online unlock there; lockout 5 wrong → 5 min, 10 → online; auto-lock after 5 minutes idle; the session lives in memory only ([ADR-0028 §4](../adr/0028-field-devices-offline-data-shared-tablet.md#4-offline-pin-unlock-us-p1-off-04-iam-03-ac3)).

## 12.6 Storage & quota

- Request `navigator.storage.persist()`; monitor quota; compress photos client-side (max 1600 px, ~200 KB).
- Purge synced blobs after server confirmation + N days.

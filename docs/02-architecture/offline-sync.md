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
├── outbox            signed, immutable commands awaiting upload (ordered, with retry metadata)
├── blobs             photos/signature images (compressed WebP/JPEG, chunked upload)
└── sync_state        per-collection cursors, last success, device clock offset
```

## 12.2 Sync protocol

1. **Push:** `POST /sync/v1/batches` with up to N commands; each has `commandId` (UUIDv7) used as **Idempotency-Key**. Server responds per command: `ACCEPTED | DUPLICATE | REJECTED(reason) | CONFLICT(details)`.
2. **Blobs:** uploaded first via pre-signed URLs; commands reference blob hashes; server verifies hash on receipt.
3. **Pull:** `GET /sync/v1/changes?since=<cursor>&scopes=...` returns deltas for reference data and statuses of the user's own submissions.
4. **Ordering:** per-device monotonic sequence; server enforces it per device to detect lost/replayed batches.

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
- Shared tablets: fast user switching via PIN with per-user local encryption key wrap; session expiry per shift.

## 12.6 Storage & quota

- Request `navigator.storage.persist()`; monitor quota; compress photos client-side (max 1600 px, ~200 KB).
- Purge synced blobs after server confirmation + N days.

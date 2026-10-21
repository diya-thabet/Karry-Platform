# Spike WP-P0-10 / WP-P0-11 — Offline capture & device keys (PWA)

**Questions.**
1. (WP-P0-10) Does an IndexedDB outbox (Dexie) plus a Workbox service worker keep signed field entries and photos across reloads and restarts, start offline, and sync **exactly once**?
2. (WP-P0-11) Can a **non-extractable** ECDSA P-256 key persist in IndexedDB, sign offline, and be verified server-side?

**Status:** ✅ Answered in desktop Chromium (Playwright 1.64.1, Vite 8.3.5, vite-plugin-pwa 2.0.0, Dexie 4.4.6), 2026-10-21. ⏳ The physical-device part (2 GB Android tablet, 72 h airplane mode, 300 photos, reboot, low battery) remains a **manual device-lab run**; checklist in the [Phase 0 tracker](../../docs/04-phases/phase-00-discovery/tracker.md).

## What was built

- [`src/db.ts`](src/db.ts): Dexie schema `outbox` (immutable signed commands), `blobs` (photos by SHA-256), `identity` (device key).
- [`src/device.ts`](src/device.ts): enrollment generates a key pair with `extractable: false`; only the public JWK leaves the device.
- [`src/outbox.ts`](src/outbox.ts): `capture()` canonicalizes (RFC 8785), hashes, signs and writes command + photo **in one IndexedDB transaction** with a gap-free device sequence; `sync()` uploads in order and marks only acknowledged commands as synced.
- [`tests/support.ts`](tests/support.ts): a fake `/sync/v1/batches` server that verifies signatures and hashes, deduplicates by `commandId`, enforces sequence order, and models network outages.

## Results

| Test | Verifies | Result |
|---|---|---|
| TC-P0-OFF-001 | App shell starts offline after first visit (service worker) | ✅ |
| TC-P0-OFF-002 | 1,000 entries + 50 photos × 200 KB captured offline survive reload; sequence gap-free | ✅ (~3.5 s) |
| TC-P0-OFF-003 | Outbox survives full browser restart | ✅ |
| TC-P0-OFF-004 | Offline sync fails without loss; after reconnect each command stored once; re-sync sends nothing | ✅ |
| TC-P0-OFF-005 | Replayed batch (lost ack) deduplicated by `commandId` | ✅ |
| TC-P0-KEY-001 | Private key not exportable (`InvalidAccessError`) | ✅ |
| TC-P0-KEY-002 | Same key after 10 browser restarts; signatures verify each time | ✅ |
| TC-P0-KEY-003 | Signature over canonical payload verifies server-side (Node, P1363) | ✅ |
| TC-P0-KEY-004 | Tampered payload or signature rejected | ✅ |
| TC-P0-KEY-005 | "Clear site data" deletes the key → app reports `NOT_ENROLLED`, capture refused | ✅ |

**Mutation check:** making the key extractable fails TC-P0-KEY-001; not advancing the device sequence fails TC-P0-OFF-002/004/005.

## Findings & decisions for Phase 1

1. Pattern confirmed: **command + photo + sequence increment in one IndexedDB transaction**; mark synced only on explicit server ack.
2. Key loss on "clear site data" is expected browser behaviour → Phase 1 needs the **re-enrollment flow** (US-P1-OFF-02) and a server alert when a device re-enrolls with unsynced history.
3. Playwright's `context.setOffline` does not block routed requests, so test servers must model outages themselves (done in `FakeSyncServer`).
4. TypeScript 7 (native compiler) worked without changes for this code size; re-confirm with React + TanStack in Phase 1.
5. `navigator.storage.persist()` is requested on start; whether it is granted depends on the device/browser and must be checked on the device lab tablets.

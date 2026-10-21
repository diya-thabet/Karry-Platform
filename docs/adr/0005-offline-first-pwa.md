---
title: "ADR-0005: Offline-first PWA with Dexie outbox and idempotent sync"
area: adr
tags: [offline, pwa, sync, dexie, service-worker]
source: "Phase 0 WP-P0-16"
status: proposed
last_updated: 2026-10-21
---

# ADR-0005 — Offline-first PWA with Dexie outbox and idempotent sync

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (to be accepted by the owner at the Phase 0 exit gate, M0)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** WP-P0-10; spike [field-capture-pwa](../../spikes/field-capture-pwa/README.md); US-P1-OFF-*, US-P2-OFF-*

## Context

Quarries often have no or intermittent connectivity. Field capture (shifts, fuel, tickets) must never stop or lose data, on low-cost Android tablets. App-store distribution and MDM are not available to most customers.

## Options considered

1. Native apps (Kotlin/Swift) — **Pros:** Best device access. **Cons:** Two codebases, store distribution, slower iteration.
2. Online-only web app — **Pros:** Simple. **Cons:** Unusable at sites; data loss.
3. **PWA, local-first with IndexedDB outbox** (chosen) — **Pros:** One codebase, instant updates, works offline; spike proved persistence and exactly-once sync. **Cons:** Browser storage can be cleared; no Background Sync on iOS.

## Decision

Field apps are PWAs (React + TypeScript) with a Workbox service worker for the app shell and Dexie (IndexedDB) for reference data, drafts, an outbox of immutable signed commands, and photo blobs. Each capture writes command + photo + device sequence increment in **one IndexedDB transaction**. Sync uploads in device-sequence order in batches; the server deduplicates by `commandId` (UUIDv7) and returns per-command status; the client marks commands synced only on explicit acknowledgement. Sync triggers: foreground, `online` event, timer, manual. Background Sync is an enhancement only. The server supports the current and two previous client versions.

## Consequences

- ✅ Spike evidence (desktop Chromium): 1,000 entries + 50 photos survive reload/restart; offline start; exactly-once sync; replay deduplicated.
- ⚠ Physical-device validation (2 GB tablet, 72 h) still required (device lab).
- ⚠ "Clear site data" deletes local data and keys, so a re-enrollment flow and quarantine of unsynced data on revoked devices are required.
- 🔎 Revisit (Capacitor wrapper) if MDM/kiosk/Bluetooth scale needs arise.

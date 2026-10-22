---
title: "ADR-0016: Site edge agent for weighbridge and plant integration"
area: adr
tags: [edge, weighbridge, iot, opc-ua, integration]
source: "Phase 0 WP-P0-16"
status: accepted
last_updated: 2026-10-22
---

# ADR-0016 — Site edge agent for weighbridge and plant integration

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Accepted (owner, 2026-10-22)
- **Date:** 2026-10-21
- **Deciders:** Product owner, software architect
- **Related:** Phase 6 (US-P6-EDG-*), Phase 9 (US-P9-EDG-*)

## Context

Manual weight entry is the last big fraud vector at the weighbridge. Indicators speak serial/TCP protocols, belt scales and PLCs speak Modbus/OPC UA, and the internet at sites is unreliable.

## Options considered

1. Browser Web Serial from the PWA — **Pros:** No extra hardware. **Cons:** Not supported everywhere; no operation when the tablet is off; weak tamper resistance.
2. Third-party IoT platform — **Pros:** Ready-made. **Cons:** Cost, lock-in, another vendor in the trust chain.
3. **Karry edge agent (.NET worker) on a site mini-PC** (chosen) — **Pros:** Protocol plugins, local buffer and UI during outages, signed readings, mTLS. **Cons:** Hardware logistics and remote updates.

## Decision

Ship a .NET worker service (`Karry.EdgeAgent`) for Linux/Windows mini-PCs: indicator protocol plugins with stable-weight detection, local SQLite buffer, local LAN UI for ticketing during internet outages, readings signed by the agent key, mTLS to the cloud, signed auto-updates with rollback. Later: ANPR, barrier control, OPC UA/Modbus for belt scales and PLCs.

## Consequences

- ✅ Removes manual weights; new revenue (edge add-on, hardware bundles).
- ⚠ Needs a hardware compatibility list, installer partners, remote support.
- Status: Proposed for Phase 6; detailed protocol choices depend on pilot hardware (WP-P0 open question 3).

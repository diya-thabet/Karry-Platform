---
title: "Integrations & Edge"
area: architecture
tags: [edge-agent, weighbridge, telematics, erp, weather]
source: "MASTER_PLAN.md §17"
status: baseline
last_updated: 2026-10-21
---

# Integrations & Edge

> [Docs index](../README.md) › [02-architecture](README.md)

## 17.1 Site Edge Agent (Phase 6+, optional per site)

- Small .NET worker on an industrial mini-PC at the weighbridge/office.
- Reads weighbridge indicators (RS-232/RS-485 serial, TCP, common protocols of major indicator brands), optional ANPR camera for plates, barrier/printer control.
- Buffers locally (SQLite) during outages; signs readings with its device key; pushes via mTLS.
- Remote management: versioned updates, health heartbeat, config from cloud.

## 17.2 Telematics (Phase 9+)

- Ingest OEM telematics via **ISO 15143-3 (AEMP 2.0)** APIs (CAT, Komatsu, Volvo, etc.) for hours, fuel, location, fault codes.
- Direct CANbus / J1939 via third-party gateways (MQTT) when OEM data is unavailable.
- Telematics data **cross-checks** manual meter readings (strong fraud signal) rather than replacing the human workflow on day one.
- Plant PLCs/belt scales via **OPC UA** / Modbus through the edge agent.

## 17.3 External systems

| Integration | Approach |
|---|---|
| Weather | Adapter over Open-Meteo (forecast + historical); provider swappable |
| Accounting/ERP | Export journals & invoices: CSV/Excel templates, Odoo & Sage connectors (Phase 10), generic webhook |
| Notifications | Push (Web Push), email (SES/Postmark), SMS/WhatsApp Business via aggregator for low-connectivity regions |
| Public API | Versioned REST + webhooks for enterprise customers (Phase 11) |

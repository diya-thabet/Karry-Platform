---
title: "System Architecture (C4)"
area: architecture
tags: [c4, containers, layers, sequence]
source: "MASTER_PLAN.md §8"
status: baseline
last_updated: 2026-10-21
---

# System Architecture (C4)

> [Docs index](../README.md) › [02-architecture](README.md)

## 8.1 Context diagram (C4 Level 1)

```mermaid
flowchart LR
    subgraph Users
      OP[Field Operator]
      WM[Weighmaster]
      CT[Shift Controller]
      SK[Storekeeper]
      MP[Maintenance Planner]
      EX[Executive / CFO]
      AD[Tenant Admin]
    end

    KARRY[(Karry Platform)]

    OP & WM & CT & SK & MP & EX & AD --> KARRY

    KARRY <--> IDP[Keycloak / Enterprise SSO]
    KARRY <--> WX[Weather Provider]
    KARRY <--> LLM[LLM Provider / Self-hosted model]
    KARRY <--> ACC[Accounting / ERP Export]
    KARRY <--> NOTIF[SMS / WhatsApp / Email / Push]
    EDGE[Site Edge Gateway<br/>weighbridge, telematics] <--> KARRY
    SCALE[Weighbridge Indicator] --> EDGE
    TEL[Machine Telematics / CANbus] --> EDGE
```

## 8.2 Container diagram (C4 Level 2)

```mermaid
flowchart TB
    subgraph Client["Clients"]
      PWA["Field PWA<br/>React + Dexie + Service Worker"]
      WEB["Web Portal<br/>(Controller, Manager, Executive, Admin)"]
    end

    subgraph Edge["Site Edge (optional)"]
      GW["Edge Agent (.NET worker)<br/>serial/Modbus/MQTT, local buffer"]
    end

    subgraph Cloud["Karry Cloud (per region)"]
      GWAPI["API Gateway / Ingress<br/>(TLS, WAF, rate-limit)"]
      API["Karry Core API<br/>.NET 10 Modular Monolith"]
      JOBS["Background Workers<br/>outbox, RUL, alerts, anchoring, reports"]
      SYNC["Sync Endpoint<br/>(batch upload, delta pull)"]
      AIGW["AI Gateway<br/>tools, guardrails, audit"]
      PY["Analytics Sidecar<br/>Python FastAPI (forecast, anomaly, Weibull)"]
      PG[("PostgreSQL<br/>+ PostGIS + TimescaleDB + pgvector")]
      RD[("Redis / Valkey")]
      S3[("Object Storage<br/>photos, PDFs, WORM anchors")]
      KMS["KMS / Vault<br/>server signing keys"]
      KC["Keycloak"]
    end

    PWA -->|HTTPS + OIDC| GWAPI
    WEB -->|HTTPS + OIDC| GWAPI
    GW -->|mTLS| GWAPI
    GWAPI --> API
    GWAPI --> SYNC
    SYNC --> API
    API --> PG
    API --> RD
    API --> S3
    API --> KMS
    API --> AIGW
    JOBS --> PG
    JOBS --> PY
    JOBS --> S3
    AIGW --> API
    AIGW --> LLM[(LLM Provider)]
    PY --> PG
    PWA -.OIDC.-> KC
    WEB -.OIDC.-> KC
```

## 8.3 Internal layering of each module (Clean / Hexagonal)

```
Module (e.g., Karry.Maintenance)
├── Domain          Entities, Value Objects, Aggregates, Domain Events, Domain Services (pure, no I/O)
├── Application     Commands, Queries, Handlers, Validators, Ports (interfaces), DTOs
├── Infrastructure  EF Core mappings, repositories, external adapters, outbox publishers
└── Api             Endpoint groups, request/response contracts, authorization policies
```

Rules (enforced by architecture tests):

- `Domain` references nothing but `Karry.SharedKernel`.
- Modules never reference each other's `Domain`/`Infrastructure`; they integrate via **public contracts** (`Karry.<Module>.Contracts`) and **integration events**.
- Each module owns its **PostgreSQL schema** (`maintenance.*`, `inventory.*`, …). No cross-schema foreign keys except to `tenancy` and `identity` reference tables.

## 8.4 Key runtime flow — field entry to verified ledger

```mermaid
sequenceDiagram
    autonumber
    participant OP as Operator (PWA, offline)
    participant IDB as IndexedDB (Dexie)
    participant SY as Sync API
    participant FO as FieldOps Module
    participant VF as Verification Module
    participant AU as Audit Ledger
    participant CT as Controller (Web)

    OP->>IDB: Create ShiftEntry (UUIDv7, device time, GPS, photo)
    OP->>IDB: Sign (signature strokes + ECDSA device signature over canonical hash)
    Note over IDB: Outbox status = PENDING_UPLOAD
    IDB->>SY: Batch upload when online (Idempotency-Key per entry)
    SY->>FO: Validate schema, verify device signature, check continuity
    FO->>AU: Append EntrySubmitted (hash-chained)
    FO-->>VF: Event: EntrySubmitted
    VF->>CT: Appears in Pending Review queue
    CT->>VF: Approve / Reject / Correct (with reason + signature)
    VF->>AU: Append EntryApproved / EntryCorrected(before, after)
    VF-->>FO: Event: EntryVerified
    FO-->>IDB: Delta pull: status VERIFIED
```

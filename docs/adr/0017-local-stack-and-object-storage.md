---
title: "ADR-0017: Local stack with Docker Compose and SeaweedFS as the self-hosted S3 store"
area: adr
tags: [local-dev, docker-compose, object-storage, s3, object-lock, minio, seaweedfs]
source: "Phase 1 sub-phase P1.0"
status: proposed
last_updated: 2026-10-22
---

# ADR-0017 — Local stack with Docker Compose and SeaweedFS as the self-hosted S3 store

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed
- **Date:** 2026-10-22
- **Deciders:** Product owner, software architect
- **Related:** US-P1-PLT-02, US-P1-AUD-02, ADR-0008, [technology stack](../02-architecture/technology-stack.md)

## Context

Developers and CI need the whole system locally: PostgreSQL with extensions, Keycloak, an S3-compatible object store **with Object Lock** (WORM anchors of the audit ledger, ADR-0008), Redis/Valkey, a mail catcher and an observability stack. The baseline plan named **MinIO** for local and on-premises object storage.

**New fact (verified 2026-10-22):** the MinIO open-source repository `minio/minio` is **archived** (no longer maintained since April 2026, README points to the commercial "AIStor" editions) and no community images are published on Docker Hub any more. Building on it would mean unpatched software in the stack and a licensing question for on-premises customers.

## Options considered

### Orchestration
1. **Docker Compose** (chosen): portable, works the same in CI, no IDE or SDK lock-in. **Cons:** less .NET-specific tooling.
2. .NET Aspire AppHost: nice dashboard and service discovery. **Cons:** ties orchestration to .NET, extra moving part for the frontend and Python sidecar. Kept **optional** for developers who want it.

### Self-hosted S3 store (local, CI, on-premises)
1. **SeaweedFS** (chosen): Apache-2.0, actively maintained, mature (≈ 35k GitHub stars), implements S3 **Object Lock** (retention, legal hold; `weed/s3api/s3api_object_retention.go`). **Cons:** more components than MinIO for large clusters (not relevant locally).
2. RustFS: Apache-2.0, active, Object Lock implemented. **Cons:** young (1.0 released in 2026); fallback if SeaweedFS fails our tests.
3. Garage: AGPL-3.0, no full Object Lock. Rejected.
4. MinIO / AIStor: archived OSS or commercial licence. Rejected.

Cloud environments keep using the provider's S3 with Object Lock (AWS S3, Cloudflare R2, Scaleway, OVH …), chosen in P1.17.

## Decision

- Local stack = `deploy/docker/compose.yaml` (Docker Compose). Aspire is optional and not required by any script or CI job.
- Self-hosted S3 = **SeaweedFS** with Object Lock enabled on the `anchors` bucket. Application code uses only the **standard S3 API** (AWS SDK for .NET) behind a storage port, so the store can be swapped by configuration.
- **Acceptance test before relying on it (P1.1, TC-P1-PLT-004):** in compliance mode, deleting or overwriting a locked object version must fail, and shortening retention must fail. If SeaweedFS does not pass, switch to RustFS and record it here.
- Container readiness is checked over TCP (Phase 0 finding), never by a socket probe.

## Consequences

- ✅ No unmaintained component in the stack; same S3 code path locally and in the cloud.
- ✅ On-premises customers can get a fully open-source (Apache-2.0) object store.
- ⚠ The Object Lock behaviour of the local store must be tested, not assumed (done in TC-P1-PLT-004).
- ⚠ Docs that mentioned MinIO are updated to "S3-compatible store (SeaweedFS locally)".

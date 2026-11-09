---
title: "ADR-0033: Signature envelopes v1 and server keys: OpenBao transit behind a port, asynchronous countersignature"
area: adr
tags: [signatures, envelope, countersignature, openbao, kms, keys, rotation, handwriting, trust, phase-2]
source: "Phase 2 sub-phase P2.0 (decision for P2.3)"
status: proposed
last_updated: 2026-11-09
---

# ADR-0033 — Signature envelopes v1 and server keys

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (2026-11-09, P2.0)
- **Date:** 2026-11-09
- **Deciders:** Product owner, software architect
- **Related:** US-P2-SIG-01 … 04; TC-P2-SIG-001 … 004, 006, 007, 008; threats T-01, T-03, T-06, T-07;
  [ADR-0007](0007-signature-envelope.md) (envelope: device signature + server countersignature + optional RFC 3161),
  [ADR-0008](0008-hash-chained-ledger.md) (ledger), [ADR-0028](0028-field-devices-offline-data-shared-tablet.md)
  (device keys), [ADR-0031](0031-sync-command-processing.md), [ADR-0032](0032-evidence-blobs.md),
  [trust layer §13.2](../02-architecture/trust-layer-signatures-audit.md#132-signature-envelope), plan amendments A4
  and A8

## Context

[ADR-0007](0007-signature-envelope.md) decided the shape: a record is signed on the device by an enrolled key, the
server countersigns with a key it holds, and an RFC 3161 timestamp can be added by policy. Phase 2 makes it real for
the first signed records (shift entries, checklists, controller actions). What is still open:

1. the **exact bytes** that are signed, so that a verifier in ten years computes the same hash;
2. how a **handwritten** signature is bound to the record;
3. **where the server key lives** before a cloud KMS exists (P1.17 has no provider yet), and how it rotates;
4. whether countersigning is **in the upload** or later, given that a signing service can be down.

## Decision

### 1. Envelope v1: what is signed

The device signs the RFC 8785 canonical form (strict mode,
[trust layer §13.2.1](../02-architecture/trust-layer-signatures-audit.md#1321-canonical-json-as-implemented-p16))
of:

```json
{
  "v": 1,
  "envelopeId": "uuidv7",
  "recordType": "fieldops.shift_entry",
  "recordId": "uuidv7",
  "recordVersion": 1,
  "payloadHash": "sha256:<hex of RFC 8785 of the record's payload>",
  "evidence": ["sha256:<hex>", "…"],
  "handwritten": { "strokesHash": "sha256:<hex>", "imageHash": "sha256:<hex>" },
  "signer": { "userId": "uuid", "role": "operator", "deviceId": "uuid" },
  "context": { "deviceTime": "ISO 8601 with offset", "appVersion": "…", "geo": { "lat": 0, "lon": 0, "acc": 8 } }
}
```

- `deviceSignature`: ES256 (IEEE P1363, base64url) by the device key, the same key and format as device requests
  ([ADR-0028](0028-field-devices-offline-data-shared-tablet.md)). It is checked against the device's stored public
  key when the command is handled (ADR-0031).
- `evidence` lists the content hashes of the blobs ([ADR-0032](0032-evidence-blobs.md)), and `geo` is optional
  (only when the site requires a geofence). Absent members are left out, never `null`, so the canonical form is
  unambiguous.
- The version `v` is part of what is signed. A later envelope version is a new verifier branch; v1 envelopes stay
  verifiable forever.

### 2. Handwritten signatures: vectors and an image, both hashed

The signature pad keeps the **strokes as vectors** (`[{t, x, y, p}]` per stroke, normalised to the pad's size, JSON)
and renders a **PNG**. Both are blobs ([ADR-0032](0032-evidence-blobs.md)), and both hashes are in the signed payload.
The vectors are the evidence (they show speed and order and can be replayed); the PNG is what a PDF prints. The pad
refuses a "signature" below minimums (at least 2 strokes and 40 points, and a bounding box of at least 20 % of the
pad; TC-P2-SIG-008). A tap is not a signature.

### 3. The server key behind a port: OpenBao transit now, cloud KMS later

The server never holds a private key in its process memory or configuration. It signs through a port in the Trust
module:

```csharp
public interface IServerSigner
{
    string KeyId { get; }                                   // the current key version, e.g. "karry-server-v3"
    Task<ServerSignature> SignAsync(ReadOnlyMemory<byte> digest, CancellationToken ct);   // ES256 over SHA-256
}
```

- **Locally, in CI and on-premises:** the **OpenBao transit** secrets engine (an `ecdsa-p256` key, sign with
  `prehashed: true` and `marshaling_algorithm: jws` so the answer is already IEEE P1363). OpenBao is the open-source
  (MPL-2.0) fork of HashiCorp Vault; Vault itself is now under the BSL. It is reached by its plain HTTP API, so no SDK
  is needed (the version is checked in P2.0). It is already the local stand-in for signing in the project's cloud plan.
- **In the cloud (P1.17):** an adapter for the provider's KMS (an asymmetric P-256 key, never exportable), behind the
  same port. Nothing else changes.
- **Rotation by `kid`:** a new key version signs from its creation; old versions stay **verify-only forever**. Their
  public keys are published in `trust.server_keys (kid, public_key, created_at, retired_at)`, which is append-only.
  A verifier picks the key by the `kid` in the countersignature.
- **Custody before a KMS:** OpenBao runs with persistent storage; its unseal key is kept outside the repository and
  the stack's volumes. Production waits for the KMS (P1.17).

### 4. Countersigning is asynchronous; the ledger binds the time at once

When a command carrying an envelope is handled ([ADR-0031](0031-sync-command-processing.md)), the handler stores the
envelope with `serverCountersignature: pending` and writes the **ledger entry in the same transaction**. The ledger
entry already binds the server's receipt time to the envelope hash, through the hash chain and its WORM anchors. An
outbox job then countersigns (`SignAsync` over the envelope hash plus `receivedAt`) and stores the countersignature.

- Target: countersigned within **5 minutes** (metric `karry_trust_countersign_lag_seconds`, alert at 15 minutes).
- If the signing service is down, the field keeps working: entries are accepted and countersigned later
  (TC-P2-SIG-006). The state `pending` / `countersigned` is visible to the controller (plan amendment A4).
- Envelopes are **append-only** for the application role (trigger, and `UPDATE`/`DELETE` revoked, as for the
  ledger; TC-P2-SIG-007). The countersignature is a separate row, written once, keyed by the envelope.

### 5. No RFC 3161 timestamp in Phase 2

There is no consumer in Phase 2, and a TSA is an external dependency and a cost. Timestamps come with weighbridge
tickets in Phase 3, per tenant policy (plan amendment A8). The envelope keeps room for `timestampToken` outside the
signed part, as in ADR-0007.

## Options considered

1. **OpenBao transit behind `IServerSigner`, asynchronous countersignature (chosen).**
2. **A software key in configuration (as the P1.6 anchoring key is locally).** Simple, but the key sits in the
   process and on disk, and rotation is manual. Acceptable for anchoring on a laptop; not for signatures that
   carry legal weight. Rejected.
3. **HashiCorp Vault.** The same API, but under the BSL since 2023. Rejected for on-premises customers; OpenBao is
   API-compatible.
4. **Countersign inside the upload request.** The answer would carry the countersignature, but every signing
   outage would stop the field. Rejected (A4).
5. **Only an image of the handwritten signature.** It is smaller, but it can be pasted from another record and
   proves nothing about how it was drawn. Rejected: vectors and image, both hashed.

## Consequences

- **Positive:** the server key never leaves the signing service. The cloud move is one adapter. A signing outage
  never stops a shift. Every envelope stays verifiable after rotations.
- **Negative:** one more service in the local stack (OpenBao), with its own unseal. The countersignature lags by
  up to minutes, and screens must show `pending`. The envelope format is now a long-term contract.
- **Follow-ups:** P2.0 checks the OpenBao image version and licence. P2.3 builds the port, the OpenBao adapter, the
  key table, the envelope store, the outbox job, the signature pad and TC-P2-SIG-001 … 008. P1.17 adds the KMS
  adapter.

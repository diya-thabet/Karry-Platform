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

### 6. As built (P2.3, 2026-11-09)

- **Slice 1, the server key:** `IServerSigner` (`SignAsync` over a 32-byte SHA-256 digest → `ServerSignature(kid,
  64-byte P1363)`, `KeysAsync` → every version's `kid`, SPKI PEM and creation time), `ServerSignatures.Verify` (needs
  only a published key) and `ServerSignerUnavailableException` in Trust.Application. Adapter `OpenBaoTransitSigner`
  (Trust.Infrastructure, plain HTTP, no SDK) and settings `Trust:ServerSigning {Address, Token, Mount = transit,
  KeyName = karry-server, CreateKeyIfMissing, Timeout = 10 s}`; no address → `UnavailableServerSigner` (nothing is
  countersigned), an address without a token does not start. Registered in the API and in Karry.Workers.
- **Change from §3:** the `kid` comes **with each signature**, not from a `KeyId` property: OpenBao signs with the
  latest version, and only its answer says which one (`vault:v{n}:…`), so a property read before signing could name
  the wrong version after a rotation. `kid` = `{KeyName}-v{n}`.
- **Verified on OpenBao 2.7.2** (the pinned image, `OpenBaoTransitSignerTests` with Testcontainers in dev mode):
  `prehashed: true`, `hash_algorithm: sha2-256`, `marshaling_algorithm: jws` gives 64 bytes that verify with the
  version's public key; after `rotate`, the new version signs and old signatures still verify with the old key
  (TC-P2-SIG-004 at adapter level); a refused connection, a timeout, `429` or `5xx` (sealed) is "unavailable" (retry
  later), a `4xx` such as a wrong token is a configuration error.
- **Slice 2, OpenBao in the local stack:** service `openbao` in `deploy/docker/compose.yaml` (127.0.0.1:8200), the
  pinned 2.7.2 image with **raft** storage on the `baodata` volume (OpenBao 2.x removed the `file` storage that §3's
  card named; one raft node is its single-server form). `deploy/docker/openbao/start.sh` initialises it once (one
  unseal key; key and root token in the separate `baokeys` volume, never in the repository or the data volume),
  unseals it on every start, creates the `ecdsa-p256` key `karry-server` (not exportable), writes the policy
  `karry-signer` (`update` on `transit/sign/karry-server`, `read` on `transit/keys/karry-server`, nothing else) and
  creates or renews the API's periodic token (`KARRY_OPENBAO_SIGNER_TOKEN`, a known local value). The healthcheck
  passes only once unsealed and ready. Checked: the key is the same after a restart; the API's token signs and reads
  the public key, and gets `403` on rotate, export, a new key and `sys/`. The smoke test checks signing and two
  refusals. `Trust:ServerSigning` is set in both hosts' development settings. Rotation:
  [runbook](../06-operations/runbooks/karry-server-key-rotation.md).
- **Slice 3a, envelope v1 on both sides:** `@karry/crypto` `envelopeProblem`, `payloadHash`, `sha256Text`,
  `signEnvelope` (checks, then signs the RFC 8785 text with the device key) and `verifyEnvelope`; .NET
  `SignatureEnvelopes` in Trust.Contracts (`Problem`, `Canonical`, `Hash`, `PayloadHash`, `VerifyDevice` against the
  device's stored P-256 point), so the module that handles a record's command can check its envelope. **Strict
  rules** (both sides): exactly the members of §1, optional ones absent rather than `null`; lowercase UUIDs;
  `recordType` `module.record_name`; `recordVersion` an integer ≥ 1; hashes `sha256:` + 64 lowercase hex; evidence
  1–20 distinct hashes when present; handwriting both hashes; `deviceTime` ISO 8601 with an offset; `geo` in range.
  Shared vectors `tests/vectors/signature-envelope.json` from an independent Python implementation
  (`generate-signature-envelope-vectors.py`): 2 valid envelopes (text, hash, a device signature both sides verify,
  and a one-character change that must not) and 22 malformed ones both sides refuse. The .NET patterns use `\z` and
  `[0-9]` (not `$` and `\d`) to match JavaScript; a mutation back to `$` is caught by the vectors.
- **Slice 3b, storing envelopes:** `IEnvelopeRecorder` (Trust.Contracts, so a module's command handler can call
  it) with `PostgresEnvelopeRecorder`, in the command's transaction: checks the v1 rules, that `signer.deviceId` is
  the device that sent the command, and the device signature against that device's key (`IDeviceDirectory`); then
  writes `audit.signature_envelopes` (the canonical text exactly as signed, its hash, record, device, command,
  `receivedAt`; append-only by trigger and grants, RLS) and the ledger entry `trust.envelope_received.v1`
  (`{envelopeId, envelopeHash, recordType, recordId, recordVersion, deviceId, commandId, receivedAt}`), which binds
  the receipt time at once (§4). The same envelope again is `AlreadyRecorded` (nothing written); refusals, final for
  the command: `trust.envelope_invalid`, `trust.envelope_device_mismatch`, `trust.envelope_signature_invalid` (event
  4010, security) and `trust.envelope_conflict` (the same ID with other content, event 4011, security).
  Tests: `EnvelopeRecorderTests` (TC-P2-SIG-002 and TC-P2-SIG-007 on real PostgreSQL). Table named
  `audit.signature_envelopes` (the Trust module's schema is `audit`), not `trust.signature_envelope` as in the card.
- **Slice 3c, countersigning:** what is signed is SHA-256 of the RFC 8785 text
  `{"envelopeHash","receivedAt","v":1}` with `receivedAt` in UTC to the microsecond (`Countersignatures` in
  Trust.Contracts; the database's precision, so the stored row always rebuilds the signed text). `CountersignEnvelopes`
  (Trust.Application), run by `CountersignJob` in Karry.Workers **every 30 seconds**: per tenant, pending envelopes
  oldest first, batches of 100, at most 10 per tenant and run; a key version not yet published is first added to
  `audit.server_keys` (global, append-only: public keys, so anyone can verify from the database alone); the
  countersignature goes in `audit.envelope_countersignatures` (one per envelope, append-only, RLS; a second worker's
  duplicate is dropped and the first stands). When the service does not answer, the run stops signing, envelopes stay
  pending, the lag is still measured (event 4012). Metrics `karry_trust_countersigned_total`,
  `karry_trust_countersign_failures_total`, `karry_trust_countersign_lag_seconds`; alerts `KarryCountersignLagHigh`
  (> 15 min for 5 min) and `KarryCountersignFailing` (> 5 failed runs in 15 min, for 10 min), with promtool tests;
  runbook [signing service down](../06-operations/runbooks/karry-signing-service-down.md). Tests:
  `CountersignTests` with PostgreSQL and OpenBao (TC-P2-SIG-001, TC-P2-SIG-004 end to end, TC-P2-SIG-006,
  TC-P2-SIG-007 for both tables), `CountersignMetricsTests`.
- **Slice 4, verification:** `GET /api/v1/audit/envelopes/{id}/verification` (the Trust module's routes live under
  `/api/v1/audit`, not `/api/v1/trust` as in the card), permission `ledger.verify`, 404 `trust.envelope_not_found`
  for an ID that is not in the caller's company (RLS scopes the read). `VerifyEnvelope` (Trust.Application) checks,
  each on its own so a failure names what broke: `envelope` (valid v1, stored in canonical form, stored hash is its
  hash), `device_signature` (the tablet's key from `IDeviceDirectory`; a revoked tablet's key still verifies what it
  signed before), `server_countersignature` (`pending` while not yet signed; `fail` when the key version is not in
  `audit.server_keys` or the signature does not match), `ledger_entry` (names this envelope's hash, payload and entry
  hash recomputed, linked to the entry before it or to genesis), `record_payload` (`not_checked` until records exist
  in P2.4). Status `verified`, `pending` or `invalid`. To find the ledger entry without scanning, the envelope row now
  stores the ledger event ID (`ledger_event_id`, migration `EnvelopeLedgerEvent`, nullable for rows from before;
  the recorder sets it in the same transaction). Tests: `EnvelopeVerificationTests` (TC-P2-SIG-001 pending then
  verified, another company gets 404; TC-P2-SIG-002 five changes made in the database behind the application's back,
  each named by the check it breaks).
- **Still to build in P2.3:** the SignaturePad (TC-P2-SIG-008), the portal "Verified" badge, and the close-out
  (security review, ADR accepted).

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

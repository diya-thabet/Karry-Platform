---
title: "ADR-0032: Evidence blobs: content-addressed, uploaded by pre-signed PUT with a checksum, retained under Object Lock"
area: adr
tags: [sync, evidence, photos, blobs, s3, presigned-url, checksum, object-lock, exif, privacy, phase-2]
source: "Phase 2 sub-phase P2.0 (decision for P2.2)"
status: proposed
last_updated: 2026-11-09
---

# ADR-0032 — Evidence blobs: content-addressed, pre-signed PUT with a checksum, Object Lock

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (2026-11-09, P2.0)
- **Date:** 2026-11-09
- **Deciders:** Product owner, software architect
- **Related:** US-P2-OFF-01, US-P2-OFF-02 (AC1–AC3), RULE-FO-05; TC-P2-SYNC-011, 012, 014; threats T-03, T-09, T-17,
  T-18; [ADR-0017](0017-local-stack-and-object-storage.md) (SeaweedFS, Object Lock),
  [ADR-0031](0031-sync-command-processing.md) (commands and structural rejections),
  [ADR-0026](0026-asset-registry-meters-and-labels.md) (asset photos), plan amendments A1 and A5
  ([Phase 2 sub-phases §6](../04-phases/phase-02-field-operations/sub-phases.md#6-amendments-to-the-phase-2-plan))

## Context

From Phase 2 a field entry carries evidence: a photo of the meter, the operator's handwritten signature (strokes and
an image), later a photo of a damaged part. The evidence is taken offline, can wait days on the tablet, and must
reach the server **unchanged** and **exactly once**. Once there, it must stay unchanged for years: it is what a
controller or an auditor looks at when an entry is questioned.

Phase 1 already stores asset photos through the API (`PUT /assets/{id}/photo`, the bytes go through Karry.Host).
Evidence is different: it is far more frequent (every entry, at shift change, 300 tablets), it is part of a
signed fact, and it must be retained even if the record that points at it is later corrected.

The questions: how the bytes travel, how their integrity is proved, how big they may be, where and how long they are
kept, and what makes a photo trustworthy.

## Decision

### 1. Content-addressed keys

A blob's key is its hash: `tenants/{tenantId}/devices/{deviceId}/evidence/{sha256}`, where the hash is the
lowercase hex SHA-256 of the exact bytes. The command that uses the blob carries the hash in its **signed** payload
(ADR-0031). So:

- the signature covers the evidence: changing the photo changes the hash, and the hash is no longer what was signed;
- the same photo sent twice is the same object: retries are free and idempotent;
- a key can never point at other bytes, because the key is the content.

### 2. Upload by pre-signed `PUT` with the checksum the store verifies

1. The tablet asks `POST /api/v1/sync/blobs/presign` with `{sha256, size, contentType}` (device-signed, like every
   device request). The server checks the size (≤ 1 MB), the type (`image/jpeg`, `image/webp`, `image/png`, and
   `application/json` for signature strokes), and the tablet's rate limit. It answers a **pre-signed `PUT` URL**
   for the content-addressed key, valid for **15 minutes**, signed with the required header
   `x-amz-checksum-sha256` (base64 of the same hash) and the exact `Content-Length`.
2. The tablet `PUT`s the bytes directly to the object store with that header. The store recomputes the SHA-256 and
   refuses the upload if it differs. The bytes never pass through Karry.Host, so the API is not the bottleneck at
   shift change.
3. If the object already exists (a retry), the presign call answers `{exists: true}` and the tablet skips the upload.
4. When the command that uses the blob is handled, the server checks that the object exists, with the expected size
   and checksum (`HEAD`, checksum mode). A missing or mismatching blob is a **structural** rejection,
   `rejected(sync.blob_missing, retryable: true)` (ADR-0031 §3). The tablet uploads again and re-sends.

**Verified in P2.2 (2026-11-09, SeaweedFS 4.48, AWSSDK.S3 4.0.104.4):** SeaweedFS honours
`x-amz-checksum-sha256` on a pre-signed `PUT`, so the store-side check is used and no server-side fallback is
built. `tests/Karry.BuildingBlocks.IntegrationTests/PresignedChecksumTests.cs` pins the behaviour:

| Case | Store answer | Stored? |
|---|---|---|
| Bytes match the signed checksum | `200` | Yes |
| Other bytes, signed checksum header | `400 BadDigest` | No |
| Other bytes, header changed to their checksum | `403 SignatureDoesNotMatch` (the header is signed) | No |
| URL used after it expired | `403` | No |
| `HEAD` with checksum mode | returns `Content-Length` and `ChecksumSHA256` | — |

The checksum header must be put in the presign request's `Headers` so it is part of the signature. The fallback
(the handler reads the object once and compares the hash) stays documented for a store that does not honour it;
the test above must pass on any store before it is used (on-premises included).

**Pre-signed URL misuse (TC-P2-SYNC-012):** the URL is valid for one key, one size, one checksum and 15 minutes.
Another key, other bytes or a late use is refused by the store. Another tenant's key cannot be requested: the
server builds the key from the device's own tenant and ID and never from the request.

### 3. At most 1 MB, so no multipart

Photos are compressed on the tablet to about 200 KB (US-P2-OFF-02 AC1: long edge 1600 px, WebP or JPEG quality 0.7,
in a worker). The limit is **1 MB per blob**. A single `PUT` is enough, so there is no multipart upload and no
resumable protocol (plan amendment A1). Multipart would add failure modes (orphaned parts, part ordering,
completion calls) for a case that cannot happen.

### 4. Retention: Object Lock on the evidence bucket

Evidence lives in its own bucket (`karry-evidence`, separate from `karry-anchors` and from the asset photos), with
**Object Lock in compliance mode** and a default retention of **5 years**. That is the default until the legal
question of WP-P0-06 is answered. A retention can be extended, never shortened. A correction of an entry never
deletes its evidence: the old version keeps pointing at it.

- **Orphans:** a blob uploaded for a command that is never sent (the tablet was wiped) cannot be deleted before its
  retention ends. The cost is small (≤ 1 MB, rare). A daily job lists orphans older than 7 days for the metric
  `karry_evidence_orphans`; nothing is deleted.
- **Reading:** evidence is read through the API (`GET /api/v1/evidence/{recordType}/{recordId}/{n}`), which checks
  the reader's permission and scope on the record, then answers a **pre-signed `GET` valid for 5 minutes**. A
  bucket URL is never stored in a record or shown to a user.

### 5. A photo is trusted because of the signed capture record, not EXIF

The field app takes photos **only through its own camera screen** (no gallery picker for evidence). At capture it
writes a **capture record** `{sha256, capturedAt (device time), deviceId, appVersion, width, height}`, which is part
of the payload the device key signs. The capture record is the proof that the photo came from the app at that time
on that device.

**EXIF is stripped** before hashing (the compression re-encodes the image, which drops it). EXIF is trivial to
forge, so it proves nothing, and it can carry the GPS position of a person's home. The site position, when needed,
comes from the entry's own signed context (geofence), not from the photo (plan amendment A5).

### 6. On the tablet: never purge before the command is confirmed

A blob stays in the tablet's store until **the command that references it** is answered `accepted`, `duplicate` or
`conflict` (TC-P2-SYNC-011). A `rejected` command keeps its blob. One upload runs at a time across the app's tabs
(Web Locks API, TC-P2-SYNC-014), so two tabs never upload the same outbox.

### 7. As built (P2.2, 2026-11-09)

- **Endpoint:** `POST /api/v1/sync/blobs/presign` (device-signed; a revoked tablet gets `401`), body
  `{sha256, size, contentType}` with the SHA-256 as 64 lowercase hex characters (the key is built from it, so
  nothing else can reach a path). Answer `{exists, key, url, headers, expiresAt}`: `exists: true` (and no URL) when
  the store already holds that content with that size; otherwise the URL and the headers to send exactly as given
  (`Content-Type` and `x-amz-checksum-sha256`, both signed). A malformed body is `400 request.invalid`; no evidence
  bucket configured is `422 sync.blobs_unavailable`; a store that does not answer is `503 server.unavailable`.
- **Code:** `IEvidenceStore` / `S3EvidenceStore` and `EvidenceKeys` in `Karry.BuildingBlocks.Storage`; `SyncBlobs`
  and `PresignBlobValidator` in Sync.Application. Signing is local (no call to the store); `HEAD` asks for the
  checksum.
- **Configuration:** `Storage:Evidence:Bucket` (its own bucket, for Object Lock), `Storage:Evidence:PublicUrl` (the
  address the tablets reach the store at; the host is signed, so it must be the public one, not the API's internal
  address), `Storage:Evidence:UploadValidFor` (default 15 minutes). Endpoint and keys are those of `Storage`.
- **Tests:** `SyncBlobsTests` (host, SeaweedFS: upload with the URL, `exists` on retry, other bytes and another key
  refused, per-tablet and per-company keys, validation, tablets only) and `SyncBlobRulesTests`.
- **The bucket (slice 2):** created on first use where `CreateBucketIfMissing` is set (local stack, CI) with Object
  Lock on, default retention **compliance** for `Storage:Evidence:RetentionDays` (1826, 5 years), and a CORS rule:
  `PUT` with `content-type` and `x-amz-checksum-sha256` from `Storage:Evidence:AllowedOrigins` (one `*` per origin;
  locally `http://*.localhost:5174`, every company's field address). An existing bucket is left as it is, so a
  setting never lowers a retention. Verified on SeaweedFS 4.48 (`EvidenceStoreTests`): deleting a retained version is
  `403 AccessDenied`; a `PUT` to the same key makes a new version and the retained one stays readable; a preflight
  from another origin is refused. In production the bucket is infrastructure as code with the same settings (waits
  for the hosting provider).
- **The check in the upload (slice 3):** a command's signed payload names its blobs in `evidence:
  [{sha256, size, contentType}]` (up to 20; other members, such as the capture record, are the handler's). Before
  its handler runs, the server `HEAD`s each one under the tablet's key: missing → `rejected(sync.blob_missing,
  retryable)`; another size, checksum or type → `rejected(sync.blob_mismatch)` and log event 7208 (security), the command kept in its place in the sequence (ADR-0031 §3 amendment);
  malformed → `rejected(sync.evidence_invalid)`; no store or a store that does not answer →
  `rejected(sync.blobs_unavailable, retryable)`. A stored command's blobs are kept in `sync.command_evidence`
  (append-only, RLS; which command uses which content), the registry the orphan job and evidence reads will use.
  Tests: `SyncBlobsTests` (TC-P2-SYNC-004, TC-P2-SYNC-005 server side), `SyncBlobRulesTests`. The `HEAD` calls run
  inside the upload's transaction (a few per command, each bounded by the store's 20 s budget); if they prove slow
  at shift change they can be made before the lock.
- **Still to build in P2.2:** the tablet's blob store and uploader (its E2E test also proves the CORS rule from a
  real browser).

## Options considered

1. **Pre-signed `PUT` with a checksum, content-addressed (chosen).** The bytes bypass the API, integrity is checked
   by the store, retries are idempotent, and the key is bound to the signature.
2. **Upload through the API** (as asset photos do). It is simple, but Karry.Host carries every byte at shift change
   (300 tablets × 10 photos in 15 minutes), and memory and request limits become the bottleneck. Rejected for
   evidence; kept for asset photos, which are rare.
3. **Random keys with the hash stored beside them.** A retry creates a duplicate object, and the key does not prove
   the content. Rejected.
4. **Multipart or resumable uploads.** Not needed at ≤ 1 MB (§3). Rejected.
5. **Trust EXIF** (RULE-FO-05 as first written). It can be forged and it leaks location. Rejected (amendment A5).

## Consequences

- **Positive:** the API is not on the byte path, a changed photo breaks the signature, retries cost nothing, and
  evidence is retained even through corrections and insider access to the database.
- **Negative:** a second network call per blob (presign) and a dependency on the store honouring the checksum
  (verified for SeaweedFS in P2.2; another store must pass the same test). Evidence that is never used cannot be deleted before its retention ends.
  The 5-year default may need to change after the legal answer; it can only go up for blobs already stored.
- **Follow-ups:** P2.2 builds the presign endpoint, the evidence bucket with its retention, the `HEAD` check in the
  pipeline, the tablet's blob store and upload queue, and TC-P2-SYNC-011/012/014. The owner answers the retention
  period (WP-P0-06).

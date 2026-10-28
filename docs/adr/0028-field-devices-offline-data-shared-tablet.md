---
title: "ADR-0028: Field tablets as enrolled devices: device-signed requests, delta reference data, offline PIN unlock"
area: adr
tags: [offline, pwa, devices, enrollment, ecdsa, webcrypto, dexie, sync, pin, shared-tablet, quarantine]
source: "Phase 1 sub-phase P1.15"
status: proposed
last_updated: 2026-10-28
---

# ADR-0028 — Field tablets as enrolled devices: device-signed requests, delta reference data, offline PIN unlock

> [Docs index](../README.md) › [adr](README.md)

- **Status:** Proposed (P1.15 slice 1, 2026-10-28)
- **Date:** 2026-10-28
- **Deciders:** Product owner, software architect
- **Related:** US-P1-OFF-01…04, US-P1-IAM-03 AC2/AC3; TC-P1-OFF-001…005, TC-P1-OFF-008, TC-P1-IAM-004;
  [ADR-0005](0005-offline-first-pwa.md) (offline-first PWA), [ADR-0007](0007-signature-envelope.md) (signature
  envelope), [ADR-0012](0012-identity-authorization.md) (identity), [offline-sync](../02-architecture/offline-sync.md),
  Phase 0 spike [field-capture-pwa](../../spikes/field-capture-pwa/README.md) (D26: IEEE P1363 signatures)

## Context

A site tablet is shared by several operators, most of whom have no e-mail address and no Keycloak account: they are
field users with an employee ID and a 6-digit PIN (P1.8, P1.14). The tablet must work for days without network, and
everything it later sends must be attributable to a trusted device. The questions:

1. **Who is the API caller** when an operator with only a PIN works on a tablet, online or offline.
2. **How a tablet becomes trusted**, and how trust is withdrawn (lost tablet), including for data it uploads after.
3. **How an operator unlocks offline** without the tablet holding something that gives away the PIN.
4. **How the tablet's reference data stays fresh** with little bandwidth, including things that leave its scope
   (a machine moved to another site, a deactivated operator).
5. **What happens when the browser forgets everything** ("clear site data" deletes the key, Phase 0 finding).

## Decision

### 1. The tablet is an enrolled device, the API caller is the device

- The field app no longer signs people in through Keycloak. A tablet is **enrolled once** for one site and from then on
  calls the API **as the device**. Operators unlock the app with employee ID + PIN; what they record is attributed to
  them **and** signed by the device (field facts, Phase 2). Office staff use the portal (it works on a phone, D44).
- The `karry-field` Keycloak client is removed from the realm when the field app stops using it (slice 5): an unused
  client with redirect URIs is attack surface.

### 2. Enrollment (US-P1-OFF-02)

- A person with `identity.devices.manage` (Tenant Admin: all sites; Site Manager: the sites they manage) creates an
  **enrollment code for a site**: 80 random bits, shown as a QR code of
  `https://<company>.<field address>/enroll#<code>` and as 16 Crockford base-32 characters to type
  (`XXXX-XXXX-XXXX-XXXX`, Crockford: `O`/`0`, `I`/`L`/`1` read alike). **Valid 15 minutes, single use**, stored
  only as a SHA-256 hash under the tenant's row-level security. The tablet names its company by the slug of its own
  address (as the sign-in page does); the code is the secret. 80 random bits for 15 minutes cannot be guessed, so no
  rate limit is needed on top.
- The tablet generates an **ECDSA P-256 key pair with `extractable: false`** (WebCrypto), keeps the `CryptoKey` in
  IndexedDB, and posts `{tenant (slug), code, publicKey (JWK), proof, name, model, platform, appVersion}`. `proof` is the device's
  signature over `karry-enroll:<code>`: the server checks that the caller holds the private key. Signatures are
  **IEEE P1363** (`r‖s`, 64 bytes), which is also the JWS ES256 format (D26).
- The server creates the device bound to **tenant and site** and answers `{deviceId, tenantId, siteId, pinKey}`
  (`pinKey`: 256 random bits, see §4). An expired or used code answers `422 devices.enrollment_expired`; an unknown
  one `404 devices.enrollment_unknown` (TC-P1-OFF-002). Enrollment and revocation are ledger events.
- **Device list** (name, model, app version, site, enrolled at, last seen, last sync, status) and **revoke**.

### 3. Device-signed requests

- Each request carries `Authorization: Device <compact JWS>`: header `{alg: ES256, typ: karry-device+jwt, kid:
  <deviceId>}`, payload `{tid, iat, exp, jti, htm, htu}`: tenant, issue and expiry time (at most 5 minutes apart),
  unique ID, HTTP method and path. The server verifies the signature with the stored public key, the expiry against
  **its own clock** (a tablet's clock may be wrong: on `401 devices.clock_skew` the answer carries the server time
  and the app corrects its offset), refuses a `jti` the device already used (remembered until the assertion expires; after that the expiry refuses it)
  and a method/path that does not match.
- The device principal carries `tenant_id`, `sub = device:<id>`, `device_id` and `site_id`, so tenant resolution,
  RLS and the host check work unchanged. **Device principals reach only endpoints marked for devices** (`sync`,
  device unlock); every user endpoint keeps requiring a user token.
- **Revoked device:** every device endpoint answers `401 devices.revoked`, except the upload endpoint, which still
  **stores** what the device sends with status `quarantined` (for review) and answers `403 devices.revoked`
  (US-P1-OFF-02 AC3, TC-P1-OFF-005). Nothing a revoked device sends is ever processed.

### 4. Offline PIN unlock (US-P1-OFF-04, IAM-03 AC3)

- The server **never sends a PIN hash** to a tablet. At enrollment the tablet receives `pinKey`, imports it as a
  **non-extractable HMAC-SHA256 key**; the server keeps it with the device row (tenant row-level security). It is
  no extra exposure there: the same database already holds the PIN hashes the verifiers are made from. For each active field
  user of the device's site, the reference data carries `{userId, employeeId, name, salt, iterations, verifier}` with
  `verifier = HMAC(pinKey, PBKDF2-SHA256(pin, salt, iterations))` (the stored hash's own salt and iterations, so the
  server computes it from the hash it keeps, without the PIN).
- To unlock, the tablet computes PBKDF2 (WebCrypto) and the HMAC with `pinKey` and compares. **A copy of the tablet's
  database cannot be brute-forced without `pinKey`**, which the browser never exports; brute force through the app is
  limited by the lockout.
- **Lockout per user on a tablet:** 5 wrong PINs lock that user for 5 minutes; 10 require an online unlock.
- **Online unlock** `POST /identity/devices/unlock {employeeId, pin}` (device-signed): the server checks the PIN
  against the stored hash with its own lockout, and the tablet records the time. **Offline unlock is allowed for 72 h
  after the user's last online unlock on that tablet** (first use on a tablet is online). Defaults 72 h and auto-lock
  after 5 minutes of inactivity, delivered by the server with the device's settings (configurable).
- A PIN change or a deactivation reaches the tablet with the next delta: the user's verifier changes or disappears,
  so a deactivated operator **cannot unlock after the next sync** (TC-P1-IAM-004).

### 5. Reference data and delta pulls (US-P1-OFF-03)

- `GET /sync/v1/changes?cursor=…` (device-signed) answers the **collections of the device's scope**: its site and
  shift patterns, the site's machines and their meters, the company's active materials, the site's field users (with
  verifiers), plus the device settings. Each owning module implements **`ISyncSource`** (BuildingBlocks, like
  `IImportTarget`): the Sync module never reads another module's tables.
- A delta returns rows **changed since the cursor across the tenant**, each marked `upsert` (in scope) or `remove`
  (out of scope: moved, archived, deactivated). Rows are ordered by `(changed_at, id)`; the cursor restarts **5
  minutes before** the last row (a transaction that committed late with an earlier timestamp is not missed; the
  client applies rows idempotently). The tablet also takes a **full snapshot every 24 h** (or when the server answers
  `410 sync.cursor_expired`), which replaces each collection. The last successful refresh time is shown.
- **Dexie schema v1** (`@karry/offline`): `device`, `settings`, `sites`, `shiftPatterns`, `assets` (indexed by code,
  label code, name words), `meters`, `materials`, `users` (indexed by employee ID), `syncState`, `unlocks`,
  `outbox` (empty until Phase 2 commands, schema fixed now). Offline lists read IndexedDB only (TC-P1-OFF-004:
  2,000 machines + 500 users in under 500 ms).

### 6. Uploads (preparing Phase 2)

- `POST /sync/v1/batches` accepts commands `{commandId (UUIDv7), sequence, type, payload, deviceTime, signature}`;
  the signature is over the **RFC 8785** canonical command (TypeScript canonicalizer checked against
  `tests/vectors/rfc8785.json`, the same vectors as the server's). Duplicates by `commandId`, a per-device gap-free
  sequence, and storage in `sync.inbox`. P1 has no command types yet: commands are stored, not processed;
  Phase 2 adds the handlers.

### 7. Install, updates and lost data (US-P1-OFF-01, TC-P1-OFF-008)

- PWA with **vite-plugin-pwa** (Workbox precache, proven in the spike). A new version is **offered only on the home
  screen**, never during a form. `navigator.storage.persist()` is requested; a refusal shows a warning.
- **Re-enrollment:** if the key or database is gone (cleared site data, new browser), the app shows "enrol this
  tablet" again; the new enrollment creates a new device; the old one stays in the list, and the server refuses its
  key once revoked. A re-enrollment while the server has unsynced history of the old device is flagged in the list.

## Options considered

| Option | Why not |
|---|---|
| Operators get Keycloak accounts, the tablet holds their refresh tokens | People without e-mail, shared tablet, offline days: refresh tokens on a shared tablet are worse than a device key; PIN cannot be a Keycloak credential without custom SPI |
| Send the PBKDF2 hash to the tablet | A copied database gives away the PIN (6 digits ≈ 20 bits) by brute force |
| Bearer device token issued at enrollment | A copied token works anywhere; a non-extractable signing key cannot be copied |
| Full snapshot on every pull | ≈ 1 MB per pull for a big site; too much on 3G |
| Keyset on `changed_at` without overlap | Misses rows of transactions that commit late with an earlier timestamp |

## Consequences

- ✅ Only enrolled tablets can call the device endpoints; a lost tablet is cut off by one click and its later uploads
  are kept for review, not processed.
- ✅ The tablet works offline for days; operators switch with their PIN; a copied tablet database reveals no PIN.
- ✅ The field app has no Keycloak dependency; one less client in the realm.
- ⚠ PBKDF2 with 600 000 iterations takes about a second per unlock on a low-end tablet (to measure on the reference
  device); acceptable for a user switch.
- ⚠ A tablet that stays offline more than 72 h needs network for the next unlock of each user (by design).
- ⚠ Office staff cannot use the field app; they use the portal.

---
title: "Runbook: rotate the server signing key"
area: operations
tags: [runbook, security, signatures, openbao, keys, rotation, trust]
source: "P2.3 slice 2, ADR-0033 §3 and §6, US-P2-SIG-03, TC-P2-SIG-004"
status: baseline
last_updated: 2026-11-09
---

# Rotate the server signing key

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

The server countersigns envelopes with the OpenBao transit key `karry-server`
([ADR-0033 §3](../../adr/0033-signature-envelopes-and-server-keys.md#3-the-server-key-behind-a-port-openbao-transit-now-cloud-kms-later)).
Each countersignature names the key version that made it (`kid` = `karry-server-v{n}`). Rotating adds a version:
the new one signs from then on, and every old one stays available to verify, forever.

- **When:** on a schedule set with the security review (default yearly), at once if a token able to sign may have
  leaked, and after a change of custody (someone who held the root token leaves).
- **Who:** the platform engineer, with a second person watching (the key is the company's proof of receipt).

## Steps (local stack and on-premises OpenBao)

1. Rotate, with the root token kept in the `baokeys` volume (never copy it out of the container):

   ```bash
   docker compose -f deploy/docker/compose.yaml exec openbao sh -c \
     'BAO_ADDR=http://127.0.0.1:8200 BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /openbao/keys/init.txt)" \
      bao write -f transit/keys/karry-server/rotate && \
      BAO_ADDR=http://127.0.0.1:8200 BAO_TOKEN="$(sed -n "s/^Initial Root Token: //p" /openbao/keys/init.txt)" \
      bao read -field=latest_version transit/keys/karry-server'
   ```

   The last line prints the new version number.
2. Check: the next countersignatures carry the new `kid`, and the verification of an older envelope still passes
   (portal "Verified" details, or `GET /api/v1/trust/envelopes/{id}/verification`, once envelopes exist in P2.3).
3. Write the rotation in the operations log: date, new version, the reason, who did it and who watched.

## Never

- **Never** set `min_available_version`, trim (`transit/keys/karry-server/trim`) or delete the key: old versions
  would stop verifying, and every countersignature they made would look forged.
- **Never** make the key exportable (`exportable` and `allow_plaintext_backup` stay `false`); the API's token cannot
  do it (the smoke test checks), and the root token must not either.
- **Never** give the API's token more than the `karry-signer` policy (sign with `karry-server`, read its public keys).

## In the cloud

A cloud KMS replaces OpenBao there (P1.17): rotation is a new key version in the KMS with the same rules (old
versions verify-only, never scheduled for deletion). This runbook gets a cloud section then.

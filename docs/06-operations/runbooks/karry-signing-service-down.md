---
title: "Runbook: signing service down (KarryCountersignLagHigh, KarryCountersignFailing)"
area: operations
tags: [runbook, alert, signatures, countersignature, openbao, workers, trust]
source: "P2.3 slice 3c, ADR-0033 §4, US-P2-SIG-03, TC-P2-SIG-006"
status: baseline
last_updated: 2026-11-09
---

# Signing service down

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

Every signed field entry carries an envelope. The API stores it and its ledger entry with the tablet's command, then
the Workers' countersign job signs it with the server key in OpenBao, every 30 seconds
([ADR-0033 §4](../../adr/0033-signature-envelopes-and-server-keys.md#4-countersigning-is-asynchronous-the-ledger-binds-the-time-at-once)).
Metrics: `karry_trust_countersign_lag_seconds` (age of the oldest envelope not yet countersigned),
`karry_trust_countersign_failures_total`, `karry_trust_countersigned_total`.

- **Symptom:**
  - `KarryCountersignLagHigh` (ticket): an envelope has waited more than 15 minutes, for 5 minutes (target 5).
  - `KarryCountersignFailing` (ticket): more than 5 failed runs in 15 minutes, for 10 minutes.
- **Impact:** **nothing is lost and the field keeps working**: entries are accepted, stored and ledgered (the ledger
  entry already binds the receipt time). Controllers see their envelopes as "pending" instead of "countersigned"
  until the job catches up, which it does on its own once the service answers (TC-P2-SIG-006).
- **Diagnosis:**
  1. Workers logs: event **4012** (`The signing service did not answer`) means OpenBao is down, sealed or slow; event
     **4013** (`Envelopes of tenant … could not be countersigned`) with an `InvalidOperationException` from OpenBao
     (`403`, `404`) means a configuration problem: wrong token, missing key or mount.
  2. OpenBao: `docker compose -f deploy/docker/compose.yaml ps openbao` and its logs. `wget -qO- http://127.0.0.1:8200/v1/sys/health`
     answers `200` only when unsealed and active (`503` sealed, `501` not initialised).
  3. Settings of the Workers: `Trust:ServerSigning:Address`, `Token`, `KeyName`. With no address, nothing is
     countersigned at all (the lag grows from the first envelope).
- **Mitigation:**
  - **Down or sealed:** restart the service: `docker compose -f deploy/docker/compose.yaml restart openbao`. Its start
    script unseals it with the key in the `baokeys` volume and renews the Workers' token.
  - **Token refused (`403`):** the periodic token expired (it lasts 32 days without a restart of OpenBao). A restart
    of OpenBao renews it; check `KARRY_OPENBAO_SIGNER_TOKEN` matches the Workers' `Token`.
  - **Key missing (`404` on `transit/sign/karry-server`):** OpenBao's data volume was lost. **Stop:** a new key
    cannot verify old countersignatures. Old ones still verify from `audit.server_keys` (the public keys are kept in
    the database), but restore the volume from backup before creating a new key, and record the incident.
  - Never countersign another way (no key outside OpenBao, no manual rows): an envelope stays pending until the real
    key signs it.
- **Resolution:** the next runs countersign the backlog (up to 1,000 envelopes per tenant per run), the lag drops
  under 5 minutes and both alerts clear.
- **Escalation:** ticket for the platform engineer within the working day; page if the lag passes 24 hours on a
  working site, or at once for a lost key.

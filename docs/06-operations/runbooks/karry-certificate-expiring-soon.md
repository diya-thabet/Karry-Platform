---
title: "Runbook: KarryCertificateExpiringSoon"
area: operations
tags: [runbook, alert, tls, certificates]
source: "P1.4, US-P1-OPS-02 AC3"
status: baseline
last_updated: 2026-10-23
---

# KarryCertificateExpiringSoon

> [Docs index](../../README.md) › [06-operations](../README.md) › [runbooks](README.md)

- **Symptom:** a public TLS certificate expires in **less than 14 days** for 1 hour (`probe_ssl_earliest_cert_expiry` from blackbox_exporter HTTPS probes, wired in P1.17; severity `ticket`). Certificates renew automatically about 30 days before expiry, so this alert means **automatic renewal has failed**.
- **Impact:** none yet. If it expires: the portal and API become unreachable for every browser, field tablets cannot sync (they keep capturing offline), site edge agents stop sending weighbridge readings.
- **Diagnosis:**
  1. Which host: the `instance` label. Check the served certificate: `openssl s_client -connect <host>:443 -servername <host> </dev/null 2>/dev/null | openssl x509 -noout -dates -issuer`.
  2. Renewal logs of the certificate manager (cert-manager / the hosting provider's managed certificates, chosen in P1.17): failed ACME challenge, DNS change, rate limit, expired provider credentials.
  3. DNS still points to the right load balancer? (`dig +short <host>`).
- **Mitigation:** fix the cause and trigger renewal (cert-manager: delete the failed `CertificateRequest` / re-issue; managed provider: re-validate the domain). If renewal cannot be fixed in time, install a manually issued certificate from the same CA before expiry.
- **Resolution:** add a test or check for the cause (for example a DNS change procedure that includes the ACME record), and confirm the alert clears.
- **Escalation:** ticket for the platform engineer within the working day; technical lead (owner) if less than 3 days remain.

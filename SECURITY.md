# Security policy

Karry stores operational, financial and signed field data for many companies. Security reports are handled with priority.

## Reporting a vulnerability

- **Do not open a public issue.** Use GitHub's private vulnerability reporting ("Security" tab → "Report a vulnerability") or contact the repository owner directly.
- Include: affected component and version, steps to reproduce, impact (for example cross-tenant access, signature bypass, ledger tampering), and any proof of concept.
- We acknowledge within 2 business days and give a remediation plan within 10 business days. Sev-1 issues (cross-tenant data exposure, authentication bypass, ledger/signature tampering) are fixed before any other work.

## Scope and rules

- Never test against customer tenants or production data; use a local stack (`deploy/docker`) or a staging tenant provided by us.
- No denial-of-service, social engineering or physical attacks.

## How the code is protected

Engineering rules: [`docs/00-llm/llm_guidelines.md` §10](docs/00-llm/llm_guidelines.md#10-security-policy). Threat model: [`docs/02-architecture/threat-model.md`](docs/02-architecture/threat-model.md). Every pull request runs secret scanning (gitleaks), dependency and misconfiguration scanning (Trivy) and static analysis (Semgrep) ([ADR-0021](docs/adr/0021-security-scanning-private-repo.md)).

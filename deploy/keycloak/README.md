# Keycloak realm and login theme (as code)

Sign-in for Karry (ADR-0012, sub-phase P1.8). Everything Keycloak needs is in this folder; nothing is configured by hand.

| Path | What it is |
|---|---|
| `generate_realm.py` | **Source of the realm.** Edit it, then run it; `--check` (CI) fails when the JSON was edited by hand or not regenerated. |
| `realm/karry-realm.json` | Generated: the `karry` realm (app clients, token lifetimes, brute-force protection, password policy, user profile, MFA browser flow, events). Imported on first start (`--import-realm`). |
| `upgrade_realm.py` | Brings an **existing** realm to the realm file for changes made after its first import (Keycloak never re-imports). Idempotent; `--check` lists what differs. Run it after pulling a realm change: `python3 deploy/keycloak/upgrade_realm.py` (local admin by default; `KEYCLOAK_ADMIN_USERNAME` / `KEYCLOAK_ADMIN_PASSWORD` elsewhere). A live test checks it finds a fresh import up to date. |
| `themes/karry/login/` | Login theme on top of Keycloak's `keycloak.v2`: FR (default) / EN messages, tenant branding script. Read live by Keycloak. |

## Clients

| Client | Type | Used by |
|---|---|---|
| `karry-portal` | public, Authorization Code + PKCE (S256) | Portal (`http://localhost:5173` locally) |
| `karry-api` | bearer-only | Token audience the API validates (`Authentication:Audience`) |
| `karry-identity` | confidential, service account | The Identity module calls the admin API: users (`manage-users`), organizations (`manage-realm`), login events (`view-events`), each tenant's app addresses as redirect URIs (`manage-clients`). Secret from `KARRY_IDENTITY_CLIENT_SECRET`; the `identity-local-only` default is for the local stack and tests only. **This secret is as sensitive as the Keycloak admin password**: vault only (P1.17), never in configuration files outside local development. |

App tokens carry `tenant_id` and `platform_role` from **admin-only** user attributes (users can neither see nor change
them), plus `aud=karry-api`. They carry no roles: authorization is Karry's (P1.9).

## Security settings and why

| Setting | Value | Reason |
|---|---|---|
| Access token | 5 min | US-P1-IAM-01 AC2 (≤ 10 min) |
| Refresh tokens | rotated, reuse = 0 | A reused refresh token kills the app session, including the newest token (TC-P1-IAM-003) |
| SSO session | 30 min idle, 10 h max | A shift, then sign in again |
| Brute force | 5 failures → temporary lockout, wait grows 1 → 15 min; quick-login check 1 s | AC4. Never permanent, so nobody can lock a colleague out for good. The page shows the same message as a wrong password. |
| Password policy | ≥ 12 chars, not username/e-mail, history 5 | NIST 800-63B style: length over composition rules |
| Registration | off | Users are invited (US-P1-IAM-03) |
| Second factor | role `mfa-required` ⇒ WebAuthn or TOTP; TOTP enrolment forced when none | US-P1-IAM-02. The Identity module gives the role to holders of a privileged Karry role. |
| Factors in the token | `amr` (RFC 8176) on the app clients: `pwd`, `otp`, `hwk` from each authenticator's reference config, valid for the SSO session (10 h) | ADR-0029 §5: the API refuses an operator token without `otp` or `hwk` (401 `auth.mfa_required`). Enrolling TOTP is a required action, not a use of the factor: the first token after enrolment says only `pwd`, and the portal asks to sign in again. |
| Forgotten password | `karry reset credentials`: e-mail link (5 min, single use), then **for `mfa-required` users the second factor** (or its enrolment), then the new password; no step removes a factor | Hardening plan H-I6. Keycloak's built-in flow asks no second factor and *removes* the OTP, so a mailbox alone took over a Tenant Admin. The answer page is the same for unknown accounts (no enumeration). |
| Keycloak's e-mails | `smtpServer` from `KARRY_SMTP_HOST`, `_PORT`, `_FROM`, `_STARTTLS` (import placeholders); default Mailpit (`mailpit:1025`) | Only the reset e-mail is Keycloak's (invitations are Karry's). The relay's user and password are never in this file: set them in the vault and the console (P1.17); `upgrade_realm.py` keeps them. |
| One-time passwords | TOTP, HMAC-SHA1, 6 digits, 30 s, look-ahead 1 | RFC 6238's default and the only algorithm every authenticator app computes correctly. SHA-1's collision weakness does not apply to HMAC. |
| API token check | 30 s clock tolerance, RS256 only | The framework default (5 minutes) doubled the token's life (H-I1) |
| Events | login, failures, credential changes, reset e-mails sent; 30 days | `Karry.Workers` copies failures and lockouts of known users to the tenant ledger every minute (`import-sign-ins` once) |

### Forgotten password (`karry reset credentials`)

```
username or e-mail ─► e-mail with a link (5 min) ─► the link
                         └─ if role mfa-required:
                              ├─ has WebAuthn or TOTP → ask for one of them      (karry reset configured factor)
                              └─ otherwise            → TOTP enrolment            (karry reset enrol factor)
                       ─► new password ─► signed in
```

Keycloak needs separate sub-flows and configs per parent, so the reset flow has its own copies (`karry reset …`).
`KeycloakPasswordResetTests` drives it on a live Keycloak and reads the e-mail from Mailpit.

### MFA flow (`karry browser`)

```
SSO cookie ─or─ username + password
                  └─ if role mfa-required:
                       ├─ user has WebAuthn or TOTP → ask for one of them   (karry configured factor)
                       └─ otherwise                 → TOTP enrolment first  (karry enrol factor)
```

**Operator accounts** (Karry staff, never a company's people): user attribute `platform_role=operator`, no
`tenant_id`, and the realm role `mfa-required`, so every sign-in asks for the second factor (enrolment at the first
one). Create them in the Keycloak admin console of the `karry` realm; the API checks `amr` as well. Locally,
`Karry.Host seed-demo` creates two (idempotent; it refuses an existing account that has a company): `operator@karry.test`
for a person showing the console (they enrol their own authenticator at the first sign-in) and
`e2e-operator@karry.test` for the browser tests, which keep the authenticators they enrol in the ignored
`src/frontend/e2e/.auth/totp.json`.

Keycloak fails a flow whose only steps are *alternatives none of which is configured*; that is why enrolment is a
separate conditional sub-flow (`Condition - sub-flow executed`, "not executed").

## Tenant branding on the login page

The apps send users to Keycloak with `redirect_uri=https://<slug>.<base domain>/…`. Keycloak validates it against the
client before rendering, then `karry-branding.js` takes the slug from that origin and reads
`GET <app origin>/api/v1/tenancy/public/<slug>` (anonymous, CORS `*`, no credentials). The origin is remembered for
the tab (`sessionStorage`, branding only, never a token) so pages rendered after a form post keep the branding.

The branding carries the company name, its colour and its logo address (US-P1-TEN-04, P1.11). The script uses only
values of the expected shape: the colour (`#RRGGBB`, accepted by the API only with at least 4.5:1 contrast against
white) becomes the `--karry-brand` CSS variable of the main button, the logo an image from the app origin
(`/api/v1/tenancy/public/<slug>/logo?v=…`). The browser E2E checks the button colour of demo tenant A.

## Changing the realm

- Edit `generate_realm.py`, run `python3 deploy/keycloak/generate_realm.py`; `RealmConfigurationTests` (static) and
  `KeycloakLoginTests` (live Keycloak) must pass.
- Import only happens when the realm does not exist. Locally: `docker compose -f deploy/docker/compose.yaml down -v`
  (drops all local data), or delete the `karry` realm in the admin console and restart Keycloak.
- Production imports and upgrades are a deployment step (P1.17); the Identity module creates tenant organizations
  and per-tenant redirect URIs at runtime.

## Testing against this realm

`tests/Karry.Testing/KeycloakServer.cs` starts the same image with this folder mounted (or uses the Keycloak in
`KARRY_TEST_KEYCLOAK`, e.g. `http://127.0.0.1:8080` for the compose stack). `KeycloakBrowser` drives the login pages
over HTTP (PKCE, forms, TOTP enrolment, token exchange).

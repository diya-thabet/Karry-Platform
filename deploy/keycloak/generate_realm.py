#!/usr/bin/env python3
"""Generates deploy/keycloak/realm/karry-realm.json (the Karry realm as code, ADR-0012, P1.8).

Edit this file, never the JSON: `python3 deploy/keycloak/generate_realm.py` rewrites it, `--check` fails when the
committed JSON differs (CI, tooling job). Every security value is explained in deploy/keycloak/README.md and checked by
RealmConfigurationTests (static) and KeycloakLoginTests (live Keycloak).
"""
import json
import pathlib
import sys

OUTPUT = pathlib.Path(__file__).resolve().parent / "realm" / "karry-realm.json"
admin_only = {"view": ["admin"], "edit": ["admin"]}
both = {"view": ["admin", "user"], "edit": ["admin", "user"]}
profile = {
  "unmanagedAttributePolicy": "ADMIN_VIEW",
  "attributes": [
    {"name": "username", "displayName": "${username}", "permissions": {"view": ["admin", "user"], "edit": ["admin"]}, "multivalued": False,
     "validations": {"length": {"min": 3, "max": 255}, "username-prohibited-characters": {}, "up-username-not-idn-homograph": {}}},
    {"name": "email", "displayName": "${email}", "permissions": both, "multivalued": False,
     "validations": {"email": {}, "length": {"max": 255}}},
    {"name": "firstName", "displayName": "${firstName}", "permissions": both, "multivalued": False, "required": {"roles": ["user"]},
     "validations": {"length": {"max": 255}, "person-name-prohibited-characters": {}}},
    {"name": "lastName", "displayName": "${lastName}", "permissions": both, "multivalued": False, "required": {"roles": ["user"]},
     "validations": {"length": {"max": 255}, "person-name-prohibited-characters": {}}},
    {"name": "tenant_id", "displayName": "Tenant", "permissions": admin_only, "multivalued": False,
     "validations": {"pattern": {"pattern": "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", "error-message": "tenant_id must be a lower-case UUID"}}},
    {"name": "platform_role", "displayName": "Platform role", "permissions": admin_only, "multivalued": False,
     "validations": {"options": {"options": ["operator"]}}},
  ],
  "groups": [{"name": "user-metadata", "displayHeader": "User metadata", "displayDescription": "Attributes, which refer to user metadata"}],
}

def attr_mapper(attr):
    return {"name": attr, "protocol": "openid-connect", "protocolMapper": "oidc-usermodel-attribute-mapper", "consentRequired": False,
            "config": {"user.attribute": attr, "claim.name": attr, "jsonType.label": "String", "access.token.claim": "true",
                       "id.token.claim": "false", "userinfo.token.claim": "false", "introspection.token.claim": "true", "multivalued": "false"}}

audience = {"name": "karry-api audience", "protocol": "openid-connect", "protocolMapper": "oidc-audience-mapper", "consentRequired": False,
            "config": {"included.client.audience": "karry-api", "access.token.claim": "true", "id.token.claim": "false", "introspection.token.claim": "true"}}

# ADR-0029 §5: tokens say how the person signed in (RFC 8176 `amr`), so the API can refuse an operator without a second
# factor. Keycloak takes each value from the authenticator's "reference" config below, while the SSO session lasts.
amr = {"name": "amr", "protocol": "openid-connect", "protocolMapper": "oidc-amr-mapper", "consentRequired": False,
       "config": {"access.token.claim": "true", "id.token.claim": "true", "introspection.token.claim": "true", "userinfo.token.claim": "false"}}

def reference(alias, value):
    return {"alias": alias, "config": {"default.reference.value": value, "default.reference.maxAge": "36000"}}

def app_client(client_id, name, port):
    origins = [f"http://localhost:{port}", f"http://127.0.0.1:{port}",
               f"http://demo-carriere-a.localhost:{port}", f"http://demo-carriere-b.localhost:{port}"]
    return {
      "clientId": client_id, "name": name, "enabled": True, "protocol": "openid-connect",
      "publicClient": True, "standardFlowEnabled": True, "implicitFlowEnabled": False,
      "directAccessGrantsEnabled": False, "serviceAccountsEnabled": False, "frontchannelLogout": True,
      "redirectUris": [o + "/*" for o in origins], "webOrigins": origins,
      "attributes": {"pkce.code.challenge.method": "S256", "post.logout.redirect.uris": "##".join(o + "/*" for o in origins),
                     "use.refresh.tokens": "true", "client_credentials.use_refresh_token": "false"},
      "fullScopeAllowed": False,
      "defaultClientScopes": ["web-origins", "acr", "basic", "profile", "email"],
      "optionalClientScopes": [],
      "protocolMappers": [attr_mapper("tenant_id"), attr_mapper("platform_role"), audience, amr],
    }

def flow(alias, description, top, executions):
    return {"alias": alias, "description": description, "providerId": "basic-flow", "topLevel": top, "builtIn": False,
            "authenticationExecutions": executions}

def step(authenticator, requirement, priority, config=None):
    e = {"authenticator": authenticator, "authenticatorFlow": False, "requirement": requirement, "priority": priority, "userSetupAllowed": False}
    if config:
        e["authenticatorConfig"] = config
    return e

def sub(alias, requirement, priority):
    return {"authenticatorFlow": True, "flowAlias": alias, "requirement": requirement, "priority": priority, "userSetupAllowed": False}

IDENTITY_ROLES = ["manage-users", "view-users", "query-users", "manage-realm", "view-realm", "view-events", "view-clients", "manage-clients"]
realm = {
  "realm": "karry", "displayName": "Karry", "enabled": True,
  "sslRequired": "external", "organizationsEnabled": True,
  "registrationAllowed": False, "rememberMe": False,
  # Forgotten password (hardening plan H-I6): self-service by e-mail, through "karry reset credentials" below, never
  # Keycloak's built-in flow (it asks no second factor and removes the user's OTP: the inbox alone would be enough).
  "resetPasswordAllowed": True, "resetCredentialsFlow": "karry reset credentials",
  # Keycloak's own e-mails (password reset). Placeholders are resolved at import from the environment: Mailpit on the
  # local stack, the real relay in production (P1.17 sets KARRY_SMTP_*; the relay's credentials go to the vault, never here).
  "smtpServer": {"host": "${KARRY_SMTP_HOST:mailpit}", "port": "${KARRY_SMTP_PORT:1025}", "from": "${KARRY_SMTP_FROM:no-reply@karry.test}",
                 "fromDisplayName": "Karry", "starttls": "${KARRY_SMTP_STARTTLS:false}", "ssl": "false", "auth": "false"},
  "loginWithEmailAllowed": True, "duplicateEmailsAllowed": False, "editUsernameAllowed": False, "verifyEmail": False,
  "loginTheme": "karry", "emailTheme": "keycloak",
  "internationalizationEnabled": True, "supportedLocales": ["fr", "en"], "defaultLocale": "fr",
  # US-P1-IAM-01 AC2: short access tokens, rotated refresh tokens, a reused refresh token is rejected.
  "accessTokenLifespan": 300, "accessTokenLifespanForImplicitFlow": 300, "accessCodeLifespan": 60,
  "accessCodeLifespanLogin": 1800, "accessCodeLifespanUserAction": 300,
  "ssoSessionIdleTimeout": 1800, "ssoSessionMaxLifespan": 36000,
  "clientSessionIdleTimeout": 0, "clientSessionMaxLifespan": 0,
  "offlineSessionIdleTimeout": 2592000, "offlineSessionMaxLifespanEnabled": True, "offlineSessionMaxLifespan": 5184000,
  "revokeRefreshToken": True, "refreshTokenMaxReuse": 0,
  # Invitation links (execute-actions e-mails) are valid 7 days (US-P1-IAM-03 AC1).
  "actionTokenGeneratedByAdminLifespan": 604800, "actionTokenGeneratedByUserLifespan": 300,
  # US-P1-IAM-01 AC4: temporary lockout after 5 failures, growing wait, capped at 15 minutes.
  "bruteForceProtected": True, "bruteForceStrategy": "MULTIPLE", "permanentLockout": False, "maxTemporaryLockouts": 0,
  "failureFactor": 5, "waitIncrementSeconds": 60, "maxFailureWaitSeconds": 900, "maxDeltaTimeSeconds": 43200,
  "quickLoginCheckMilliSeconds": 1000, "minimumQuickLoginWaitSeconds": 60,
  "passwordPolicy": "length(12) and maxLength(128) and notUsername(undefined) and notEmail(undefined) and passwordHistory(5)",
  "otpPolicyType": "totp", "otpPolicyAlgorithm": "HmacSHA1", "otpPolicyDigits": 6, "otpPolicyPeriod": 30, "otpPolicyLookAheadWindow": 1,
  "webAuthnPolicyRpEntityName": "Karry", "webAuthnPolicyUserVerificationRequirement": "preferred",
  "eventsEnabled": True, "eventsExpiration": 2592000,
  "enabledEventTypes": ["LOGIN", "LOGIN_ERROR", "LOGOUT", "LOGOUT_ERROR", "CODE_TO_TOKEN_ERROR", "REFRESH_TOKEN_ERROR",
                        "UPDATE_CREDENTIAL", "REMOVE_CREDENTIAL", "UPDATE_TOTP", "REMOVE_TOTP", "EXECUTE_ACTIONS",
                        "UPDATE_PASSWORD", "RESET_PASSWORD", "RESET_PASSWORD_ERROR", "SEND_RESET_PASSWORD", "SEND_RESET_PASSWORD_ERROR"],
  "adminEventsEnabled": True, "adminEventsDetailsEnabled": False,
  "browserFlow": "karry browser",
  "roles": {"realm": [{"name": "mfa-required", "description": "Set by Karry for users holding a privileged role (US-P1-IAM-02): TOTP or WebAuthn is mandatory at login."}]},
  "clients": [
    {"clientId": "karry-api", "name": "Karry API (token audience only)", "enabled": True, "protocol": "openid-connect", "bearerOnly": True,
     "publicClient": False, "standardFlowEnabled": False, "directAccessGrantsEnabled": False, "serviceAccountsEnabled": False},
    app_client("karry-portal", "Karry portal", 5173),
    {"clientId": "karry-identity", "name": "Karry Identity module (admin API)", "enabled": True, "protocol": "openid-connect",
     "publicClient": False, "clientAuthenticatorType": "client-secret", "secret": "${KARRY_IDENTITY_CLIENT_SECRET:identity-local-only}",
     "standardFlowEnabled": False, "directAccessGrantsEnabled": False, "serviceAccountsEnabled": True,
     "fullScopeAllowed": False, "attributes": {"access.token.lifespan": "120"}},
  ],
  "users": [
    {"username": "service-account-karry-identity", "enabled": True, "serviceAccountClientId": "karry-identity",
     "clientRoles": {"realm-management": IDENTITY_ROLES}},
  ],
  "clientScopeMappings": {"realm-management": [{"client": "karry-identity", "roles": IDENTITY_ROLES}]},
  "authenticatorConfig": [
    {"alias": "karry privileged role", "config": {"condUserRole": "mfa-required", "negate": "false"}},
    {"alias": "karry no configured factor", "config": {"flow_to_check": "karry configured factor", "check_result": "not-executed"}},
    reference("karry amr password", "pwd"), reference("karry amr hardware key", "hwk"),
    reference("karry amr otp", "otp"), reference("karry amr otp enrolment", "otp"),
    {"alias": "karry reset privileged role", "config": {"condUserRole": "mfa-required", "negate": "false"}},
    {"alias": "karry reset no configured factor", "config": {"flow_to_check": "karry reset configured factor", "check_result": "not-executed"}},
    reference("karry reset amr hardware key", "hwk"), reference("karry reset amr otp", "otp"), reference("karry reset amr otp enrolment", "otp")],
  "authenticationFlows": [
    flow("karry browser", "Karry browser login: SSO cookie, or password then MFA for privileged users", True, [
      step("auth-cookie", "ALTERNATIVE", 10),
      sub("karry browser forms", "ALTERNATIVE", 30)]),
    flow("karry browser forms", "Username/password, then the privileged-user MFA step", False, [
      step("auth-username-password-form", "REQUIRED", 10, "karry amr password"),
      sub("karry privileged mfa", "CONDITIONAL", 20)]),
    flow("karry privileged mfa", "US-P1-IAM-02: users with the mfa-required role must use, or first enrol, a second factor", False, [
      step("conditional-user-role", "REQUIRED", 10, "karry privileged role"),
      sub("karry configured factor", "CONDITIONAL", 20),
      sub("karry enrol factor", "CONDITIONAL", 30)]),
    flow("karry configured factor", "The user already has WebAuthn or TOTP: ask for one of them", False, [
      step("conditional-user-configured", "REQUIRED", 10),
      step("webauthn-authenticator", "ALTERNATIVE", 20, "karry amr hardware key"),
      step("auth-otp-form", "ALTERNATIVE", 30, "karry amr otp")]),
    flow("karry enrol factor", "No second factor yet: TOTP enrolment is forced before any token is issued", False, [
      step("conditional-sub-flow-executed", "REQUIRED", 10, "karry no configured factor"),
      step("auth-otp-form", "REQUIRED", 20, "karry amr otp enrolment")]),
    # H-I6: the e-mail link proves the inbox; a privileged user must then also pass (or, without one, enrol) the
    # second factor, as at sign-in, before choosing a password. No "reset OTP" step: a reset never removes a factor.
    # Keycloak needs distinct sub-flows and configs per parent, hence the "karry reset …" copies.
    flow("karry reset credentials", "Forgotten password: e-mail link, the second factor for privileged users, then the new password", True, [
      step("reset-credentials-choose-user", "REQUIRED", 10),
      step("reset-credential-email", "REQUIRED", 20),
      sub("karry reset privileged mfa", "CONDITIONAL", 30),
      step("reset-password", "REQUIRED", 40)]),
    flow("karry reset privileged mfa", "H-I6: users with the mfa-required role prove their second factor before a password reset", False, [
      step("conditional-user-role", "REQUIRED", 10, "karry reset privileged role"),
      sub("karry reset configured factor", "CONDITIONAL", 20),
      sub("karry reset enrol factor", "CONDITIONAL", 30)]),
    flow("karry reset configured factor", "The user already has WebAuthn or TOTP: ask for one of them", False, [
      step("conditional-user-configured", "REQUIRED", 10),
      step("webauthn-authenticator", "ALTERNATIVE", 20, "karry reset amr hardware key"),
      step("auth-otp-form", "ALTERNATIVE", 30, "karry reset amr otp")]),
    flow("karry reset enrol factor", "No second factor yet: TOTP enrolment is forced before any token is issued", False, [
      step("conditional-sub-flow-executed", "REQUIRED", 10, "karry reset no configured factor"),
      step("auth-otp-form", "REQUIRED", 20, "karry reset amr otp enrolment")]),
  ],
  "components": {"org.keycloak.userprofile.UserProfileProvider": [
    {"providerId": "declarative-user-profile", "subComponents": {}, "config": {"kc.user.profile.config": [json.dumps(profile, separators=(",", ":"))]}}]},
}


def main(argv):
    text = json.dumps(realm, indent=2, ensure_ascii=False) + "\n"
    if "--check" in argv:
        if OUTPUT.read_text(encoding="utf-8") != text:
            print(f"{OUTPUT} is out of date: run python3 deploy/keycloak/generate_realm.py", file=sys.stderr)
            return 1
        print(f"{OUTPUT.name} is up to date")
        return 0
    OUTPUT.write_text(text, encoding="utf-8")
    print(f"wrote {OUTPUT}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

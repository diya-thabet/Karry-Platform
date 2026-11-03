#!/usr/bin/env python3
"""Brings a running Keycloak's `karry` realm up to the realm file for the changes made after it was first imported.

Keycloak imports deploy/keycloak/realm/karry-realm.json only on first start (`--import-realm` skips an existing realm),
so an existing environment needs this script after a change to the realm. Every step is idempotent: it reads the
current state and changes only what differs. Steps so far:

  * ADR-0029 §5 (2026-10-29): the `amr` mapper on the app clients, and the reference value of every factor
    (password `pwd`, one-time password `otp`, WebAuthn `hwk`) so tokens say how the person signed in.
  * H-I6 (2026-11-03): the "karry reset credentials" flow (a forgotten password needs the second factor of a
    privileged user), Keycloak's SMTP settings (placeholders resolved from this process's environment, as an import
    resolves them), and the password-reset e-mail events.

Usage (local stack defaults; pass the real admin credentials elsewhere, never commit them):
  python3 deploy/keycloak/upgrade_realm.py [--url http://localhost:8080] [--check]
  KEYCLOAK_ADMIN_USERNAME / KEYCLOAK_ADMIN_PASSWORD override the local-only bootstrap admin.
"""
import argparse
import json
import os
import re
import sys
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate_realm  # noqa: E402  (the realm as code is the single source of the wanted values)

REALM = generate_realm.realm


class Admin:
    def __init__(self, url, username, password):
        self.url = url.rstrip("/")
        body = urllib.parse.urlencode({"grant_type": "password", "client_id": "admin-cli", "username": username, "password": password}).encode()
        with urllib.request.urlopen(f"{self.url}/realms/master/protocol/openid-connect/token", body, timeout=30) as response:
            self.token = json.load(response)["access_token"]

    def call(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        target = f"{self.url}/admin/realms/{REALM['realm']}" + (f"/{path}" if path else "")
        request = urllib.request.Request(target, data=data, method=method,
                                         headers={"Authorization": f"Bearer {self.token}", "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=30) as response:
            text = response.read()
            return json.loads(text) if text else None


FLOWS = {f["alias"]: f for f in REALM["authenticationFlows"]}
CONFIGS = {c["alias"]: c for c in REALM["authenticatorConfig"]}
RESET_FLOW = REALM["resetCredentialsFlow"]


def resolve(value):
    """A Keycloak import placeholder, `${NAME:default}`, resolved from this process's environment as an import does."""
    match = re.fullmatch(r"\$\{([A-Z0-9_]+):([^}]*)\}", value) if isinstance(value, str) else None
    return os.environ.get(match.group(1), match.group(2)) if match else value


def flow_path(alias):
    return f"authentication/flows/{urllib.parse.quote(alias)}/executions"


def wanted_shape(alias, level=0):
    """The flow tree as Keycloak lists it: (level, step or sub-flow, requirement), depth first."""
    shape = []
    for e in sorted(FLOWS[alias]["authenticationExecutions"], key=lambda e: e["priority"]):
        shape.append((level, e["flowAlias"] if e["authenticatorFlow"] else e["authenticator"], e["requirement"]))
        if e["authenticatorFlow"]:
            shape += wanted_shape(e["flowAlias"], level + 1)
    return shape


def live_shape(admin, alias):
    return [(e["level"], e["displayName"] if e.get("authenticationFlow") else e["providerId"], e["requirement"])
            for e in admin.call("GET", flow_path(alias))]


def create_flow(admin, alias):
    """Adds the steps of a (new, empty) flow in order, sets each requirement and config, and recurses into sub-flows."""
    for e in sorted(FLOWS[alias]["authenticationExecutions"], key=lambda e: e["priority"]):
        if e["authenticatorFlow"]:
            child = FLOWS[e["flowAlias"]]
            admin.call("POST", f"{flow_path(alias)}/flow",
                       {"alias": child["alias"], "description": child["description"], "provider": "registration-page-form", "type": "basic-flow"})
            name = child["alias"]
        else:
            admin.call("POST", f"{flow_path(alias)}/execution", {"provider": e["authenticator"]})
            name = e["authenticator"]
        added = next(x for x in admin.call("GET", flow_path(alias))
                     if x["level"] == 0 and (x["displayName"] if x.get("authenticationFlow") else x.get("providerId")) == name)
        admin.call("PUT", flow_path(alias), {**added, "requirement": e["requirement"]})
        if e.get("authenticatorConfig"):
            config = CONFIGS[e["authenticatorConfig"]]
            admin.call("POST", f"authentication/executions/{added['id']}/config", {"alias": config["alias"], "config": config["config"]})
        if e["authenticatorFlow"]:
            create_flow(admin, e["flowAlias"])


def reset_flow(admin, check, changes):
    realm = admin.call("GET", "")
    existing = next((f for f in admin.call("GET", "authentication/flows") if f["alias"] == RESET_FLOW), None)
    if existing is None or live_shape(admin, RESET_FLOW) != wanted_shape(RESET_FLOW):
        if existing is not None and realm.get("resetCredentialsFlow") == RESET_FLOW:
            changes.append(f"flow {RESET_FLOW} differs from the realm file while in use: compare it in the admin console")
            return
        changes.append(f"{'rebuild' if existing else 'create'} the flow {RESET_FLOW}")
        if not check:
            if existing is not None:
                admin.call("DELETE", f"authentication/flows/{existing['id']}")
            admin.call("POST", "authentication/flows", {"alias": RESET_FLOW, "description": FLOWS[RESET_FLOW]["description"],
                                                         "providerId": "basic-flow", "topLevel": True, "builtIn": False})
            create_flow(admin, RESET_FLOW)

    update = {}
    if realm.get("resetCredentialsFlow") != RESET_FLOW:
        changes.append(f"use {RESET_FLOW} for forgotten passwords")
        update["resetCredentialsFlow"] = RESET_FLOW
    if realm.get("resetPasswordAllowed") != REALM["resetPasswordAllowed"]:
        changes.append(f"set resetPasswordAllowed to {REALM['resetPasswordAllowed']}")
        update["resetPasswordAllowed"] = REALM["resetPasswordAllowed"]
    smtp = {key: resolve(value) for key, value in REALM["smtpServer"].items()}
    current = realm.get("smtpServer") or {}
    if any(current.get(key) != value for key, value in smtp.items()):
        changes.append(f"send Keycloak's e-mails through {smtp['host']}:{smtp['port']}")
        # Keys the realm file does not set (a relay's user and password, set in the console or by P1.17) are kept.
        update["smtpServer"] = {**current, **smtp}
    if update and not check:
        admin.call("PUT", "", update)

    events = admin.call("GET", "events/config")
    missing = sorted(set(REALM["enabledEventTypes"]) - set(events.get("enabledEventTypes") or []))
    if missing:
        changes.append(f"record the events {', '.join(missing)}")
        if not check:
            admin.call("PUT", "events/config", {**events, "enabledEventTypes": sorted(set(events.get("enabledEventTypes") or []) | set(missing))})


def app_clients():
    return [c for c in REALM["clients"] if any(m["protocolMapper"] == "oidc-amr-mapper" for m in c.get("protocolMappers", []))]


def amr_mappers(admin, check, changes):
    for wanted in app_clients():
        found = admin.call("GET", f"clients?clientId={urllib.parse.quote(wanted['clientId'])}")
        if not found:
            changes.append(f"client {wanted['clientId']} is missing: import the realm first")
            continue
        client_id = found[0]["id"]
        mapper = next(m for m in wanted["protocolMappers"] if m["protocolMapper"] == "oidc-amr-mapper")
        existing = [m for m in admin.call("GET", f"clients/{client_id}/protocol-mappers/models") if m["protocolMapper"] == "oidc-amr-mapper"]
        if existing:
            continue
        changes.append(f"add the amr mapper to {wanted['clientId']}")
        if not check:
            admin.call("POST", f"clients/{client_id}/protocol-mappers/models", mapper)


def factor_references(admin, check, changes):
    configs = {c["alias"]: c["config"] for c in REALM["authenticatorConfig"]}
    for flow in REALM["authenticationFlows"]:
        wanted = [e for e in flow["authenticationExecutions"] if not e["authenticatorFlow"] and e.get("authenticatorConfig") in configs
                  and "default.reference.value" in configs[e["authenticatorConfig"]]]
        if not wanted:
            continue
        try:
            current = admin.call("GET", flow_path(flow["alias"]))
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            continue  # not created yet (--check): reported by its own step
        for execution in wanted:
            alias = execution["authenticatorConfig"]
            match = next((c for c in current if c.get("providerId") == execution["authenticator"] and c.get("level") == 0), None)
            if match is None:
                changes.append(f"{flow['alias']}: no {execution['authenticator']} step to configure")
                continue
            if match.get("authenticationConfig"):
                config = admin.call("GET", f"authentication/config/{match['authenticationConfig']}")
                if config["config"].get("default.reference.value") == configs[alias]["default.reference.value"] \
                        and config["config"].get("default.reference.maxAge") == configs[alias]["default.reference.maxAge"]:
                    continue
                changes.append(f"{flow['alias']}: update the reference of {execution['authenticator']}")
                if not check:
                    admin.call("PUT", f"authentication/config/{match['authenticationConfig']}",
                               {**config, "config": {**config["config"], **configs[alias]}})
                continue
            changes.append(f"{flow['alias']}: name {execution['authenticator']} as {configs[alias]['default.reference.value']}")
            if not check:
                admin.call("POST", f"authentication/executions/{match['id']}/config", {"alias": alias, "config": configs[alias]})


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--url", default=os.environ.get("KEYCLOAK_URL", "http://localhost:8080"))
    parser.add_argument("--check", action="store_true", help="list what would change, change nothing; exit 1 when something differs")
    args = parser.parse_args(argv)
    admin = Admin(args.url, os.environ.get("KEYCLOAK_ADMIN_USERNAME", "admin"), os.environ.get("KEYCLOAK_ADMIN_PASSWORD", "admin-local-only"))
    changes = []
    amr_mappers(admin, args.check, changes)
    reset_flow(admin, args.check, changes)
    factor_references(admin, args.check, changes)
    for change in changes:
        print(("would " if args.check else "") + change)
    print("realm up to date" if not changes else f"{len(changes)} change(s){' needed' if args.check else ' made'}")
    return 1 if args.check and changes else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

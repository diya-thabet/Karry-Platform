#!/usr/bin/env python3
"""Brings a running Keycloak's `karry` realm up to the realm file for the changes made after it was first imported.

Keycloak imports deploy/keycloak/realm/karry-realm.json only on first start (`--import-realm` skips an existing realm),
so an existing environment needs this script after a change to the realm. Every step is idempotent: it reads the
current state and changes only what differs. Steps so far:

  * ADR-0029 §5 (2026-10-29): the `amr` mapper on the app clients, and the reference value of every factor
    (password `pwd`, one-time password `otp`, WebAuthn `hwk`) so tokens say how the person signed in.

Usage (local stack defaults; pass the real admin credentials elsewhere, never commit them):
  python3 deploy/keycloak/upgrade_realm.py [--url http://localhost:8080] [--check]
  KEYCLOAK_ADMIN_USERNAME / KEYCLOAK_ADMIN_PASSWORD override the local-only bootstrap admin.
"""
import argparse
import json
import os
import sys
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
        request = urllib.request.Request(f"{self.url}/admin/realms/{REALM['realm']}/{path}", data=data, method=method,
                                         headers={"Authorization": f"Bearer {self.token}", "Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=30) as response:
            text = response.read()
            return json.loads(text) if text else None


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
        current = admin.call("GET", f"authentication/flows/{urllib.parse.quote(flow['alias'])}/executions")
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
    factor_references(admin, args.check, changes)
    for change in changes:
        print(("would " if args.check else "") + change)
    print("realm up to date" if not changes else f"{len(changes)} change(s){' needed' if args.check else ' made'}")
    return 1 if args.check and changes else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

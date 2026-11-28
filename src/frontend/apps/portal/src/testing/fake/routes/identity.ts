import type { components } from "@karry/api-client";
import type { Role, User } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { supportSession } from "../factories";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** Identity: access, roles, users, support sessions and tablets (the catch-all: any other path is unknown). */
export function identityRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { roles, users, sites, devices, supportSessions } = s;
  const fieldAccount: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/identity/me/field-account") {
      return json(200, options.fieldAccount ?? { linked: true, unlinkedNamesake: false });
    }
    return null;
  };

  const identity: Route = ({ request, path, body }) => {
    const roleMatch = /^\/roles\/([^/]+)(\/field-levels)?$/.exec(path);
    switch (true) {
      case request.method === "GET" && path === "/me/access":
        return json(200, { permissions: options.permissions });
      case request.method === "GET" && path === "/permissions":
        return json(200, {
          permissions: [
            { code: "identity.users.read", module: "identity", scope: "site", privileged: false },
            { code: "identity.users.manage", module: "identity", scope: "site", privileged: true },
            { code: "identity.roles.read", module: "identity", scope: "tenant", privileged: false },
            { code: "identity.roles.manage", module: "identity", scope: "tenant", privileged: true },
            { code: "audit.ledger.read", module: "audit", scope: "site", privileged: false },
            { code: "audit.ledger.verify", module: "audit", scope: "site", privileged: false },
          ],
          sensitiveFields: [{ code: "probe.quote.unit-price", module: "probe", fallback: "masked" }],
        });
      case request.method === "GET" && path === "/roles":
        return json(200, { items: roles });
      case request.method === "POST" && path === "/roles": {
        const draft = body as components["schemas"]["CreateRole"];
        const created: Role = {
          code: "custom-0001", isDefault: false, name: draft.name, description: draft.description, basedOn: draft.basedOn, office: draft.office ?? true,
          privileged: false, permissions: draft.permissions ?? [], fieldLevels: draft.fieldLevels ?? {}, version: '"1"',
        };
        roles.push(created);
        return json(201, created);
      }
      case roleMatch !== null && request.method === "GET": {
        const role = roles.find((r) => r.code === roleMatch[1]);
        return role ? json(200, role) : problem(404, "identity.role_not_found");
      }
      case roleMatch !== null && (request.method === "PUT" || request.method === "DELETE"): {
        const index = roles.findIndex((r) => r.code === roleMatch[1]);
        const role = roles[index];
        if (!role) {
          return problem(404, "identity.role_not_found");
        }

        const ifMatch = request.headers.get("If-Match");
        if (ifMatch !== null && ifMatch !== role.version) {
          return problem(409, "request.version_conflict");
        }

        if (request.method === "DELETE") {
          roles.splice(index, 1);
          return new Response(null, { status: 204 });
        }

        return json(200, (roles[index] = { ...role, ...(body as object), version: nextVersion(role.version) }));
      }
      case request.method === "GET" && path === "/users":
        return json(200, { items: users, nextCursor: null });
      case request.method === "GET" && /^\/users\/[^/]+$/.test(path): {
        const user = users.find((u) => path.endsWith(u.id));
        return user ? json(200, user) : problem(404, "identity.user_not_found");
      }
      case request.method === "PUT" && /^\/users\/[^/]+\/roles$/.test(path): {
        const index = users.findIndex((u) => path.includes(u.id));
        const user = users[index];
        if (!user) {
          return problem(404, "identity.user_not_found");
        }

        const ifMatch = request.headers.get("If-Match");
        if (ifMatch !== null && ifMatch !== user.version) {
          return problem(409, "request.version_conflict");
        }

        return json(200, (users[index] = { ...user, roles: (body as { roles: User["roles"] }).roles, version: nextVersion(user.version) }));
      }
      case request.method === "PUT" && /^\/users\/[^/]+\/field-account$/.test(path): {
        // An office account's link to the same person's field account (ADR-0037 §4): one office account per field account.
        const index = users.findIndex((u) => path.includes(u.id));
        const found = users[index];
        if (!found) {
          return problem(404, "identity.user_not_found");
        }

        const fieldUserId = (body as { fieldUserId: string | null }).fieldUserId;
        if (fieldUserId && users.some((u) => u.id !== found.id && u.linkedFieldUserId === fieldUserId)) {
          return problem(409, "identity.field_account_linked");
        }

        users[index] = { ...found, linkedFieldUserId: fieldUserId };
        return new Response(null, { status: 204 });
      }
      case request.method === "PUT" && /^\/users\/[^/]+\/pin$/.test(path): {
        const index = users.findIndex((u) => path.includes(u.id));
        const found = users[index];
        if (!found) {
          return problem(404, "identity.user_not_found");
        }

        if (/^(\d)\1+$/.test((body as { pin: string }).pin)) {
          return problem(400, "identity.pin_too_simple");
        }

        users[index] = { ...found, pinSet: true };
        return new Response(null, { status: 204 });
      }
      case request.method === "POST" && path === "/support/consents":
        return json(201, { consentId: "0192f0c1-0000-7000-8000-00000000c001", code: "4HQX-2M7D-9RKA-TP3B", expiresAt: new Date(Date.now() + 30 * 60_000).toISOString() });
      case request.method === "GET" && path === "/support/sessions":
        return json(200, { items: supportSessions });
      case request.method === "POST" && /^\/support\/sessions\/[^/]+\/end$/.test(path): {
        const index = supportSessions.findIndex((s) => path.includes(s.id));
        const found = supportSessions[index];
        if (!found) {
          return problem(404, "support.session_not_found");
        }

        if (!found.active) {
          return problem(422, "support.session_ended");
        }

        supportSessions[index] = { ...found, active: false, endedAt: "2026-10-29T10:20:00Z" };
        return json(200, supportSessions[index]);
      }
      case request.method === "GET" && /^\/tenants\/[^/]+\/support-sessions$/.test(path):
        return json(200, { items: supportSessions.filter((s) => path.includes(s.tenantId)) });
      case request.method === "POST" && /^\/tenants\/[^/]+\/support-sessions$/.test(path): {
        const input = body as { code: string | null; reason: string | null };
        if (input.code !== null && input.code !== "4HQX-2M7D-9RKA-TP3B") {
          return problem(422, "support.consent_invalid");
        }

        if (input.code === null && (input.reason ?? "").trim().length < 30) {
          return problem(400, "support.reason_required");
        }

        const opened = supportSession({
          id: `0192f0c1-0000-7000-8000-00000000d${String(supportSessions.length + 101)}`, tenantId: path.split("/")[2] ?? "",
          mode: input.code === null ? "break_glass" : "consent", operatorName: "Ops Karry", reason: input.code === null ? input.reason : null,
          startedAt: new Date().toISOString(), endsAt: new Date(Date.now() + 60 * 60_000).toISOString(),
        });
        supportSessions.unshift(opened);
        return json(201, opened);
      }
      case request.method === "POST" && /^\/tenants\/[^/]+\/support-sessions\/[^/]+\/end$/.test(path): {
        const index = supportSessions.findIndex((s) => path.includes(s.id));
        const found = supportSessions[index];
        if (!found?.active) {
          return problem(found ? 422 : 404, found ? "support.session_ended" : "support.session_not_found");
        }

        supportSessions[index] = { ...found, active: false, endedAt: new Date().toISOString() };
        return json(200, supportSessions[index]);
      }
      case request.method === "GET" && path === "/devices":
        return json(200, devices);
      case request.method === "POST" && path === "/devices/enrollments": {
        const siteId = (body as { siteId: string }).siteId;
        return sites.some((s) => s.id === siteId)
          ? json(201, {
              enrollmentId: "0192f0c1-0000-7000-8000-00000000e001", siteId, code: "7KQ2-M9XD-4RTA-0PZB",
              url: `https://${s.tenant.slug}.field.karry.test/enroll#7KQ2-M9XD-4RTA-0PZB`, expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
            })
          : problem(400, "devices.site_invalid");
      }
      case request.method === "POST" && /^\/devices\/[^/]+\/revoke$/.test(path): {
        const index = devices.findIndex((d) => path.includes(d.id));
        const found = devices[index];
        if (!found) {
          return problem(404, "devices.not_found");
        }

        devices[index] = { ...found, status: "revoked", revokedAt: "2026-10-28T10:00:00Z" };
        return json(200, devices[index]);
      }
      default:
        return problem(404, "test.unknown_route");
    }
  };

  return [fieldAccount, identity];
}

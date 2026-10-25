import { createApiClient, type ApiClient, type components } from "@karry/api-client";

type Role = components["schemas"]["RoleResponse"];
type User = components["schemas"]["UserResponse"];

export interface Sent {
  method: string;
  path: string;
  body: unknown;
}

const fields = { "probe.quote.unit-price": "masked" };

/** The 12 default roles as the API lists them (only the permissions the tests look at). */
export function defaultRoles(): Role[] {
  const role = (code: string, office: boolean, privileged: boolean, permissions: string[]): Role => ({
    code, isDefault: true, name: null, description: null, basedOn: null, office, privileged, permissions, fieldLevels: { ...fields },
  });
  return [
    role("tenant-admin", true, true, ["identity.roles.manage", "identity.roles.read", "identity.users.manage", "identity.users.read"]),
    role("executive", true, true, ["identity.roles.read", "identity.users.read"]),
    role("site-manager", true, false, ["identity.users.manage", "identity.users.read"]),
    role("commercial", true, false, []),
    role("operator", false, false, []),
  ];
}

/**
 * An in-memory Karry API for component tests, through the real typed client (so paths, bodies and the step-up
 * problem look as in production). `needsStepUp`: while it returns true, writes answer 401 `auth.step_up_required`
 * (wire it to the test session's step-up count).
 */
export function fakeApi(options: {
  permissions: string[];
  users?: User[];
  needsStepUp?: () => boolean;
  failWith?: { status: number; code: string };
  /** Feature codes of the company's plan (default: a Professional trial). */
  features?: string[];
}) {
  const roles = defaultRoles();
  const users = options.users ?? [];
  const sent: Sent[] = [];

  const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const problem = (status: number, code: string) => json(status, { type: `https://karry.app/problems/${code}`, title: code, status, code, traceId: "trace-1" });

  const fetch = async (input: Request | URL | string, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/api/v1/billing/entitlements") {
      return json(200, {
        planCode: "professional", planVersion: 1, status: "trial", trialEndsAt: "2026-11-24T08:00:00Z",
        features: options.features ?? ["field-operations", "maintenance"], limits: { "office-users": 15 },
      });
    }

    const path = url.pathname.replace("/api/v1/identity", "");
    const body: unknown = ["POST", "PUT"].includes(request.method) ? await request.clone().json().catch(() => null) : null;
    sent.push({ method: request.method, path, body });
    if (options.failWith) {
      return problem(options.failWith.status, options.failWith.code);
    }

    if (request.method !== "GET" && options.needsStepUp?.() === true) {
      return problem(401, "auth.step_up_required");
    }

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
          privileged: false, permissions: draft.permissions ?? [], fieldLevels: draft.fieldLevels ?? {},
        };
        roles.push(created);
        return json(201, created);
      }
      case roleMatch !== null && request.method === "PUT": {
        const role = roles.find((r) => r.code === roleMatch[1]);
        return role ? json(200, { ...role, ...(body as object) }) : problem(404, "identity.role_not_found");
      }
      case roleMatch !== null && request.method === "DELETE":
        return new Response(null, { status: 204 });
      case request.method === "GET" && path === "/users":
        return json(200, { items: users, nextCursor: null });
      case request.method === "PUT" && /^\/users\/[^/]+\/roles$/.test(path): {
        const user = users.find((u) => path.includes(u.id));
        return user ? json(200, { ...user, roles: (body as { roles: User["roles"] }).roles }) : problem(404, "identity.user_not_found");
      }
      default:
        return problem(404, "test.unknown_route");
    }
  };

  const client: ApiClient = createApiClient({ baseUrl: "http://api.test", getAccessToken: () => "test-token", fetch });
  return {
    client,
    sent,
    writes: () => sent.filter((s) => s.method !== "GET"),
  };
}

export function user(overrides: Partial<User> = {}): User {
  return {
    id: "0192f0c1-0000-7000-8000-000000000001", kind: "office", status: "active", firstName: "Sami", lastName: "Gharbi",
    email: "sami@example.test", phone: null, employeeId: null, roles: [{ role: "commercial", siteId: null }], requiresMfa: false,
    createdAt: "2026-10-24T08:00:00Z", deactivatedAt: null, ...overrides,
  };
}

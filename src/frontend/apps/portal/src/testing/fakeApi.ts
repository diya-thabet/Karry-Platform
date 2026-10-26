import { createApiClient, type ApiClient, type components } from "@karry/api-client";

type Role = components["schemas"]["RoleResponse"];
type User = components["schemas"]["UserResponse"];
type Tenant = components["schemas"]["TenantResponse"];
type Site = components["schemas"]["SiteResponse"];
type SiteInput = components["schemas"]["SiteInput"];
type ShiftPattern = components["schemas"]["ShiftPatternResponse"];
type NonWorkingDay = components["schemas"]["NonWorkingDayResponse"];
type ShiftInstance = components["schemas"]["ShiftInstanceResponse"];
type Material = components["schemas"]["MaterialResponse"];
type MaterialInput = components["schemas"]["MaterialInput"];
type DensityVersion = components["schemas"]["DensityVersionResponse"];
type DensityInput = components["schemas"]["DensityInput"];
type ConversionInput = components["schemas"]["ConversionInput"];

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
  /** Every write answers this problem (reads still work). */
  refuseWrites?: { status: number; code: string };
  /** Feature codes of the company's plan (default: a Professional trial). */
  features?: string[];
  /** Flags of `GET /flags` (default: none set, so every flag has its default). */
  flags?: Record<string, boolean>;
  /** Subscription state in `GET /billing/entitlements` (default: trial). */
  status?: string;
  /**
   * The company of `GET /tenancy/current` (default: Carrière A with the default settings, except its language: none
   * unless given, so the test's own language stays).
   */
  tenant?: Partial<Tenant>;
  /** Usage lines of `GET /billing/usage` (default: 3 of 15 office users). */
  usage?: { metric: string; usage: number; limit: number | null; ceiling: number | null; state: string }[];
  /** Sites of `GET /organization/sites` (default: none). */
  sites?: Site[];
  /** Pattern versions by site ID. */
  patterns?: Record<string, ShiftPattern[]>;
  /** Non-working days by site ID. */
  days?: Record<string, NonWorkingDay[]>;
  /** What `GET /sites/{id}/shift-instances` answers for every site. */
  instances?: ShiftInstance[];
  /** Materials of `GET /materials` (default: none). */
  materials?: Material[];
}) {
  const roles = defaultRoles();
  const users = options.users ?? [];
  const sent: Sent[] = [];
  let tenant: Tenant = {
    id: "0192f0c1-0000-7000-8000-0000000000a1", slug: "carriere-a", name: "Carrière A", createdAt: "2026-10-01T08:00:00Z",
    legalName: "Carrière A", primaryColour: null, language: "none", currency: "EUR", unitSystem: "metric", timeZone: "UTC",
    fiscalYearStartMonth: 1, logoUrl: null, settingsChangedAt: null, ...options.tenant,
  };
  let changes = 0;
  const sites: Site[] = [...(options.sites ?? [])];
  const patterns: Record<string, ShiftPattern[]> = { ...options.patterns };
  const days: Record<string, NonWorkingDay[]> = { ...options.days };
  const materials: Material[] = [...(options.materials ?? [])];
  const changed = (next: Partial<Tenant>) => {
    changes += 1;
    tenant = { ...tenant, ...next, settingsChangedAt: `2026-10-26T08:00:${String(changes).padStart(2, "0")}Z` };
    return tenant;
  };

  const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const problem = (status: number, code: string) => json(status, { type: `https://karry.app/problems/${code}`, title: code, status, code, traceId: "trace-1" });

  const fetch = async (input: Request | URL | string, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    if (request.method === "GET" && url.pathname === "/api/v1/billing/entitlements") {
      return json(200, {
        planCode: "professional", planVersion: 1, status: options.status ?? "trial", trialEndsAt: "2026-11-24T08:00:00Z",
        features: options.features ?? ["field-operations", "maintenance"], limits: { "office-users": 15 },
      });
    }

    if (url.pathname === "/api/v1/tenancy/current" && request.method === "GET") {
      return json(200, tenant);
    }

    if (request.method === "GET" && url.pathname === "/api/v1/flags") {
      return json(200, { flags: options.flags ?? {} });
    }

    if (request.method === "GET" && url.pathname === "/api/v1/billing/usage") {
      sent.push({ method: "GET", path: url.pathname, body: null });
      return json(200, { items: options.usage ?? [{ metric: "office-users", usage: 3, limit: 15, ceiling: 17, state: "within" }], snapshotOn: "2026-10-25" });
    }

    if (request.method === "GET" && url.pathname === "/api/v1/billing/subscription") {
      return json(200, {
        id: "01970000-0000-7000-8000-0000000000aa", status: "trial", statusSince: "2026-10-25T08:00:00Z", trialEndsAt: "2026-11-24T08:00:00Z",
        unpaidSince: null, planCode: "professional", planVersion: 1, period: "monthly", sites: 1, extraAssets: 0, addOns: {}, extraFeatures: [], limitOverrides: {},
      });
    }

    const path = url.pathname.replace("/api/v1/identity", "");
    const isJson = request.headers.get("Content-Type")?.includes("json") ?? false;
    const body: unknown = ["POST", "PUT"].includes(request.method) && isJson ? await request.clone().json().catch(() => null) : null;
    sent.push({ method: request.method, path, body });
    if (options.failWith) {
      return problem(options.failWith.status, options.failWith.code);
    }

    if (request.method !== "GET" && options.refuseWrites) {
      return problem(options.refuseWrites.status, options.refuseWrites.code);
    }

    if (request.method !== "GET" && options.needsStepUp?.() === true) {
      return problem(401, "auth.step_up_required");
    }

    if (url.pathname === "/api/v1/tenancy/settings" && request.method === "PUT") {
      return json(200, changed(body as Partial<Tenant>));
    }

    if (url.pathname === "/api/v1/tenancy/settings/logo") {
      return json(200, changed({ logoUrl: request.method === "PUT" ? `/api/v1/tenancy/public/${tenant.slug}/logo?v=${String(changes)}` : null }));
    }

    const organization = organizationRoute(request.method, url, body);
    if (organization) {
      return organization;
    }

    const material = materialsRoute(request.method, url, body);
    if (material) {
      return material;
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

  function organizationRoute(method: string, url: URL, body: unknown): Response | null {
    const match = /^\/api\/v1\/organization\/sites(?:\/([^/]+))?(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const [, id, part, key] = match;
    if (!id) {
      if (method === "GET") {
        const all = url.searchParams.get("includeArchived") === "true";
        return json(200, { items: sites.filter((site) => all || site.status === "active") });
      }

      const created = siteFrom(`0192f0c1-0000-7000-8000-${String(sites.length + 1).padStart(12, "0")}`, body as SiteInput);
      sites.push(created);
      return json(201, created);
    }

    const index = sites.findIndex((site) => site.id === id);
    const site = sites[index];
    if (!site) {
      return problem(404, "organization.site_not_found");
    }

    const save = (next: Site) => {
      sites[index] = { ...next, changedAt: `2026-10-26T09:00:${String(index).padStart(2, "0")}Z` };
      return json(200, sites[index]);
    };
    switch (true) {
      case part === undefined && method === "GET":
        return json(200, site);
      case part === undefined && method === "PUT":
        return save({ ...siteFrom(site.id, body as SiteInput), code: site.code, status: site.status, archivedAt: site.archivedAt });
      case part === "archive":
        return save({ ...site, status: "archived", archivedAt: "2026-10-26T09:00:00Z" });
      case part === "restore":
        return save({ ...site, status: "active", archivedAt: null });
      case part === "shift-patterns" && method === "GET":
        return json(200, { items: patterns[id] ?? [] });
      case part === "shift-patterns" && method === "PUT": {
        const version: ShiftPattern = {
          id: `pattern-${key ?? ""}`, effectiveFrom: key ?? "", changedAt: "2026-10-26T09:00:00Z",
          shifts: (body as { shifts: ShiftPattern["shifts"] }).shifts,
        };
        patterns[id] = [...(patterns[id] ?? []).filter((p) => p.effectiveFrom !== key), version].sort((a, b) => a.effectiveFrom.localeCompare(b.effectiveFrom));
        return json(200, version);
      }
      case part === "shift-patterns" && method === "DELETE":
        patterns[id] = (patterns[id] ?? []).filter((p) => p.effectiveFrom !== key);
        return new Response(null, { status: 204 });
      case part === "non-working-days" && method === "GET":
        return json(200, { items: days[id] ?? [] });
      case part === "non-working-days" && method === "PUT": {
        const day: NonWorkingDay = { date: key ?? "", ...(body as { kind: string; name: string }) };
        days[id] = [...(days[id] ?? []).filter((d) => d.date !== key), day].sort((a, b) => a.date.localeCompare(b.date));
        return json(200, day);
      }
      case part === "non-working-days" && method === "DELETE":
        days[id] = (days[id] ?? []).filter((d) => d.date !== key);
        return new Response(null, { status: 204 });
      case part === "shift-instances":
        return json(200, { items: options.instances ?? [] });
      default:
        return problem(404, "test.unknown_route");
    }
  }

  function materialsRoute(method: string, url: URL, body: unknown): Response | null {
    const match = /^\/api\/v1\/materials(?:\/([^/]+))?(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const [, id, part, key] = match;
    if (!id) {
      if (method === "GET") {
        const all = url.searchParams.get("includeArchived") === "true";
        return json(200, { items: materials.filter((m) => all || m.status === "active") });
      }

      const input = body as MaterialInput;
      const created = material({
        id: `0192f0c1-0000-7000-8000-${String(materials.length + 1).padStart(12, "0")}`, code: (input.code ?? "").toUpperCase(), name: input.name ?? "",
        kind: input.kind ?? "raw-rock", densities: [versionFrom(input.effectiveFrom ?? "2026-10-26", input.densities)],
      });
      materials.push(created);
      return json(201, created);
    }

    const index = materials.findIndex((m) => m.id === id);
    const found = materials[index];
    if (!found) {
      return problem(404, "materials.material_not_found");
    }

    const save = (next: Material) => {
      materials[index] = { ...next, changedAt: `2026-10-26T09:00:${String(index).padStart(2, "0")}Z` };
      return json(200, materials[index]);
    };
    switch (true) {
      case part === undefined && method === "GET":
        return json(200, found);
      case part === undefined && method === "PUT":
        return save({ ...found, ...(body as { name: string; kind: string }) });
      case part === "archive":
        return save({ ...found, status: "archived", archivedAt: "2026-10-26T09:00:00Z" });
      case part === "restore":
        return save({ ...found, status: "active", archivedAt: null });
      case part === "densities" && method === "PUT": {
        const version = versionFrom(key ?? "", body as DensityInput);
        save({ ...found, densities: [...found.densities.filter((v) => v.effectiveFrom !== key), version].sort((a, b) => a.effectiveFrom.localeCompare(b.effectiveFrom)) });
        return json(200, version);
      }
      case part === "densities" && method === "DELETE":
        save({ ...found, densities: found.densities.filter((v) => v.effectiveFrom !== key) });
        return new Response(null, { status: 204 });
      case part === "conversions":
        return json(200, convert(found, body as ConversionInput));
      default:
        return problem(404, "test.unknown_route");
    }
  }

  const client: ApiClient = createApiClient({ baseUrl: "http://api.test", getAccessToken: () => "test-token", fetch });
  return {
    client,
    sent,
    /** Changes sent (a conversion is a POST but stores nothing). */
    writes: () => sent.filter((s) => s.method !== "GET" && !s.path.endsWith("/conversions")),
  };
}

export function user(overrides: Partial<User> = {}): User {
  return {
    id: "0192f0c1-0000-7000-8000-000000000001", kind: "office", status: "active", firstName: "Sami", lastName: "Gharbi",
    email: "sami@example.test", phone: null, employeeId: null, roles: [{ role: "commercial", siteId: null }], requiresMfa: false,
    createdAt: "2026-10-24T08:00:00Z", deactivatedAt: null, ...overrides,
  };
}

function siteFrom(id: string, input: SiteInput): Site {
  return {
    id, code: (input.code ?? "").toUpperCase(), name: input.name ?? "", type: input.type ?? "quarry",
    address: {
      line: input.address?.line ?? "", city: input.address?.city ?? "", region: input.address?.region ?? null,
      postalCode: input.address?.postalCode ?? null, country: input.address?.country ?? "",
    },
    location: { latitude: Number(input.location?.latitude ?? 0), longitude: Number(input.location?.longitude ?? 0) },
    boundary: input.boundary ? { type: "Polygon", coordinates: input.boundary } : null,
    timeZone: input.timeZone ?? "UTC", currency: input.currency ?? null, status: "active",
    createdAt: "2026-10-26T08:00:00Z", changedAt: "2026-10-26T08:00:00Z", archivedAt: null,
  };
}

/** A site as the API answers it. */
export function site(overrides: Partial<Site> = {}): Site {
  return {
    ...siteFrom("0192f0c1-0000-7000-8000-000000005001", {
      code: "CAR-NORD", name: "Carrière Nord", type: "quarry",
      address: { line: "Route de Bizerte km 12", city: "Tunis", region: null, postalCode: "1000", country: "TN" },
      location: { latitude: 36.8045, longitude: 10.0056 }, boundary: null, timeZone: "Africa/Tunis", currency: null,
    }),
    ...overrides,
  };
}

function versionFrom(effectiveFrom: string, input: DensityInput | null | undefined): DensityVersion {
  const value = (x: number | string | null | undefined) => (x === null || x === undefined ? null : Number(x));
  const bank = value(input?.bank);
  const loose = value(input?.loose);
  return {
    effectiveFrom, bank, loose, compacted: value(input?.compacted), moisture: value(input?.moisture) ?? 0,
    swell: bank !== null && loose !== null ? Math.round((bank / loose - 1) * 1e6) / 1e6 : null,
  };
}

/** A material as the API answers it: 0/31.5 with 2.2 bank, 1.6 loose, 3 % moisture from 1 January 2026. */
export function material(overrides: Partial<Material> = {}): Material {
  return {
    id: "0192f0c1-0000-7000-8000-000000006001", code: "0/31.5", name: "Grave 0/31.5", kind: "product",
    densities: [versionFrom("2026-01-01", { bank: 2.2, loose: 1.6, compacted: null, moisture: 0.03 })],
    status: "active", createdAt: "2026-10-26T08:00:00Z", changedAt: "2026-10-26T08:00:00Z", archivedAt: null, ...overrides,
  };
}

/** The engine's formula (M = V × ρ_state × (1 + w)) in floating point: enough for screens, not for golden values. */
function convert(found: Material, input: ConversionInput) {
  const on = input.on ?? "2026-10-26";
  const version = found.densities.filter((v) => v.effectiveFrom <= on).at(-1);
  if (!version) {
    return null;
  }

  const perTonne: Record<string, number> = { t: 1, kg: 0.001, st: 0.90718474 };
  const density = (state: string | null) => Number(version[state as "bank" | "loose" | "compacted"]);
  const w = Number(input.moisture ?? version.moisture);
  const quantity = input.quantity;
  const value = Number(quantity?.value);
  const factors: { name: string; value: number; unit: string }[] = [];
  let tonnes = value * (perTonne[quantity?.unit ?? ""] ?? 1);
  if (quantity?.unit === "m3") {
    factors.push({ name: `density.${quantity.state ?? ""}`, value: density(quantity.state), unit: "t/m3" });
    tonnes = value * density(quantity.state) * (1 + w);
  }

  if (input.unit !== "m3") {
    factors.push({ name: "moisture", value: w, unit: "fraction" });
    return { result: { value: tonnes / (perTonne[input.unit ?? ""] ?? 1), unit: input.unit, state: null }, factors, densitiesFrom: version.effectiveFrom };
  }

  factors.push({ name: `density.${input.state ?? ""}`, value: density(input.state), unit: "t/m3" }, { name: "moisture", value: w, unit: "fraction" });
  return { result: { value: tonnes / (density(input.state) * (1 + w)), unit: "m3", state: input.state }, factors, densitiesFrom: version.effectiveFrom };
}

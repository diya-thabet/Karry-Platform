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
type Asset = components["schemas"]["AssetResponse"];
type AssetInput = components["schemas"]["AssetInput"];
type Meter = components["schemas"]["MeterResponse"];
type Reading = components["schemas"]["ReadingResponse"];
type ImportJob = components["schemas"]["ImportResponse"];
type ImportIssue = components["schemas"]["ImportIssueResponse"];
type ImportPlannedRow = components["schemas"]["ImportRowResponse"];
type Device = components["schemas"]["DeviceResponse"];
type SupportSession = components["schemas"]["SupportSessionResponse"];
type AuditEntry = components["schemas"]["AuditEntryResponse"];
type EnvelopeVerification = components["schemas"]["EnvelopeVerification"];
type OperatorTenant = components["schemas"]["OperatorTenantResponse"];
type Verification = components["schemas"]["LatestVerificationResponse"];
type Subscription = components["schemas"]["SubscriptionResponse"];
type Invoice = components["schemas"]["InvoiceResponse"];

export interface Sent {
  method: string;
  path: string;
  body: unknown;
}

const fields = { "probe.quote.unit-price": "masked" };

/** The 12 default roles as the API lists them (only the permissions the tests look at). */
export function defaultRoles(): Role[] {
  const role = (code: string, office: boolean, privileged: boolean, permissions: string[]): Role => ({
    code, isDefault: true, name: null, description: null, basedOn: null, office, privileged, permissions, fieldLevels: { ...fields }, version: '"0"',
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
  /** Machines of `GET /assets` (default: none). */
  assets?: Asset[];
  /** Meters by asset ID. */
  meters?: Record<string, Meter[]>;
  /** Readings by meter ID, newest first. */
  readings?: Record<string, Reading[]>;
  /** What the validation of every uploaded file finds (default: one site to create, no issue). */
  importReport?: { issues: ImportIssue[]; rows: ImportPlannedRow[] };
  /** Support sessions of `GET /identity/support/sessions`, newest first (default: none). */
  supportSessions?: SupportSession[];
  /** Tablets of `GET /identity/devices` (default: none). */
  devices?: Device[];
  /** The audit log, newest first (default: empty); people's names come from the entries. */
  auditEntries?: AuditEntry[];
  /** Verifications of signature envelopes by ID (`GET /audit/envelopes/{id}/verification`); any other ID is 404. */
  envelopeVerifications?: Record<string, EnvelopeVerification>;
  /** What a run of the integrity check finds (default: an intact trail of every audit entry). */
  verifyFinds?: { valid: boolean; firstBrokenSequence: number | null; problem: string | null };
  /** The export answers `audit.export_too_large` (more than 100 000 matching entries). */
  exportTooLarge?: boolean;
  /** Companies of the operator console (`GET /tenancy/tenants`, default: none); new ones are added as trials. */
  operatorTenants?: OperatorTenant[];
  /** The operator's token has no second factor yet (`auth.mfa_required` on the operator console) while this says so. */
  operatorWithoutMfa?: () => boolean;
  /** Whether the first administrator's invitation of a new company is delivered (default: yes). */
  invitationDelivered?: boolean;
  /** Each company's subscription for the operator console, by company ID (default: a professional trial). */
  tenantSubscriptions?: Record<string, Subscription>;
  /** Each company's invoices for the operator console, by company ID (default: none). */
  tenantInvoices?: Record<string, Invoice[]>;
}) {
  const roles = defaultRoles();
  const users = options.users ?? [];
  const sent: Sent[] = [];
  let tenant: Tenant = {
    id: "0192f0c1-0000-7000-8000-0000000000a1", slug: "carriere-a", name: "Carrière A", createdAt: "2026-10-01T08:00:00Z",
    legalName: "Carrière A", primaryColour: null, language: "none", currency: "EUR", unitSystem: "metric", timeZone: "UTC",
    fiscalYearStartMonth: 1, logoUrl: null, settingsChangedAt: null, version: '"1"', ...options.tenant,
  };
  let changes = 0;
  const sites: Site[] = [...(options.sites ?? [])];
  const patterns: Record<string, ShiftPattern[]> = { ...options.patterns };
  const days: Record<string, NonWorkingDay[]> = { ...options.days };
  const materials: Material[] = [...(options.materials ?? [])];
  const assets: Asset[] = [...(options.assets ?? [])];
  const meters: Record<string, Meter[]> = { ...options.meters };
  const readings: Record<string, Reading[]> = { ...options.readings };
  const imports: ImportJob[] = [];
  const devices: Device[] = [...(options.devices ?? [])];
  const supportSessions: SupportSession[] = [...(options.supportSessions ?? [])];
  const supportCalls: { method: string; path: string; header: string | null }[] = [];
  let importSteps = 0;
  const auditCalls: { path: string; query: Record<string, string> }[] = [];
  const requested: string[] = [];
  const operatorTenants: OperatorTenant[] = [...(options.operatorTenants ?? [])];
  const tenantSubscriptions: Record<string, Subscription> = { ...options.tenantSubscriptions };
  const tenantInvoices: Record<string, Invoice[]> = { ...options.tenantInvoices };
  let verification: Verification = { verification: null, verifiedBy: null, verifiedByName: null };
  const changed = (next: Partial<Tenant>) => {
    changes += 1;
    // As the API: every saved change (the logo included) is a new version (H-C1).
    tenant = { ...tenant, ...next, settingsChangedAt: `2026-10-26T08:00:${String(changes).padStart(2, "0")}Z`, version: nextVersion(tenant.version) };
    return tenant;
  };

  const json = (status: number, body: unknown) => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
  const problem = (status: number, code: string) => json(status, { type: `https://karry.app/problems/${code}`, title: code, status, code, traceId: "trace-1" });

  const fetch = async (input: Request | URL | string, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    requested.push(url.pathname);
    // A support visit (ADR-0029 §4): the named session must be running; then reads only.
    const named = request.headers.get("Karry-Support-Session");
    supportCalls.push({ method: request.method, path: url.pathname, header: named });
    if (named !== null) {
      const visited = supportSessions.find((s) => named === `${s.tenantId}.${s.id}`);
      if (!visited?.active) {
        return problem(403, "support.session_invalid");
      }

      if (!["GET", "HEAD"].includes(request.method)) {
        return problem(403, "support.read_only");
      }
    }

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

    if (url.pathname === "/api/v1/tenancy/tenants" && options.operatorWithoutMfa?.() === true) {
      return problem(401, "auth.mfa_required");
    }

    if (url.pathname === "/api/v1/tenancy/tenants" && request.method === "GET") {
      const mrr = new Map<string, number>();
      for (const t of operatorTenants.filter((t) => t.mrr !== null && t.mrrCurrency)) {
        mrr.set(t.mrrCurrency ?? "", (mrr.get(t.mrrCurrency ?? "") ?? 0) + Number(t.mrr));
      }

      return json(200, {
        tenants: [...operatorTenants].sort((a, b) => a.name.localeCompare(b.name)),
        mrr: [...mrr].map(([currency, amount]) => ({ currency, amount })),
      });
    }

    if (url.pathname === "/api/v1/tenancy/tenants" && request.method === "POST") {
      const input = body as { slug: string; name: string; admin: { email: string } | null };
      const created = operatorTenant({ id: `0192f0c1-0000-7000-8000-0000000c${String(operatorTenants.length + 1).padStart(4, "0")}`, slug: input.slug, name: input.name, mrr: 0 });
      operatorTenants.push(created);
      return json(201, {
        id: created.id, slug: created.slug, name: created.name, createdAt: "2026-10-29T08:00:00Z",
        adminInvitation: input.admin ? { invited: true, delivered: options.invitationDelivered ?? true, error: null } : null,
      });
    }

    if (request.method === "GET" && url.pathname === "/api/v1/billing/plans") {
      return json(200, { items: ["starter", "professional", "business", "enterprise"].map((code) => ({ id: `plan-${code}`, code, version: 1, name: code, features: [], limits: {}, prices: [], publishedAt: "2026-09-01T00:00:00Z" })) });
    }

    const billed = /^\/api\/v1\/billing\/tenants\/([^/]+)\/(subscription|subscription\/actions|invoices|invoices\/([^/]+)\/payment)$/.exec(url.pathname);
    if (billed) {
      const [, tenantId = "", what, invoiceId] = billed;
      const subscription = tenantSubscriptions[tenantId] ?? (tenantSubscriptions[tenantId] = subscriptionOf());
      const invoices = tenantInvoices[tenantId] ?? (tenantInvoices[tenantId] = []);
      switch (true) {
        case request.method === "GET" && what === "subscription":
          return json(200, subscription);
        // As the API: every saved change (a state action included) is a new version; terms for an older one are refused (H-C1).
        case request.method === "PUT" && what === "subscription": {
          const ifMatch = request.headers.get("If-Match");
          if (ifMatch !== null && ifMatch !== subscription.version) {
            return problem(409, "request.version_conflict");
          }

          return json(200, (tenantSubscriptions[tenantId] = { ...subscription, ...(body as Partial<Subscription>), planVersion: 1, version: nextVersion(subscription.version) }));
        }
        case request.method === "POST" && what === "subscription/actions": {
          const status = { activate: "active", "missed-payment": "past_due", payment: "active", cancel: "cancelled" }[(body as { action: string }).action] ?? subscription.status;
          return json(200, (tenantSubscriptions[tenantId] = {
            ...subscription, status, statusSince: "2026-10-29T08:00:00Z", unpaidSince: status === "past_due" ? "2026-10-29T08:00:00Z" : null, version: nextVersion(subscription.version),
          }));
        }
        case request.method === "GET" && what === "invoices":
          return json(200, { items: invoices });
        case request.method === "POST" && what === "invoices": {
          const input = body as Omit<Invoice, "id" | "paidOn">;
          if (invoices.some((invoice) => invoice.number === input.number)) {
            return problem(409, "billing.invoice_number_taken");
          }

          const invoice: Invoice = { ...input, id: `0192f0c1-0000-7000-8000-00000000f${String(invoices.length + 1).padStart(3, "0")}`, paidOn: null };
          invoices.unshift(invoice);
          return json(201, invoice);
        }
        case request.method === "POST" && invoiceId !== undefined: {
          const invoice = invoices.find((candidate) => candidate.id === invoiceId);
          if (invoice) {
            invoice.paidOn = (body as { paidOn: string }).paidOn;
          }

          return json(200, invoice);
        }
      }
    }

    if (url.pathname === "/api/v1/tenancy/settings" && request.method === "PUT") {
      const ifMatch = request.headers.get("If-Match");
      if (ifMatch !== null && ifMatch !== tenant.version) {
        return problem(409, "request.version_conflict");
      }

      return json(200, changed(body as Partial<Tenant>));
    }

    if (url.pathname === "/api/v1/tenancy/settings/logo") {
      return json(200, changed({ logoUrl: request.method === "PUT" ? `/api/v1/tenancy/public/${tenant.slug}/logo?v=${String(changes)}` : null }));
    }

    const organization = organizationRoute(request.method, url, body, request.headers.get("If-Match"));
    if (organization) {
      return organization;
    }

    const material = materialsRoute(request.method, url, body, request.headers.get("If-Match"));
    if (material) {
      return material;
    }

    const imported = importsRoute(request.method, url);
    if (imported) {
      return imported;
    }

    const audited = auditRoute(request.method, url);
    if (audited) {
      return audited;
    }

    const machine = assetsRoute(request.method, url, body, request.headers.get("If-Match"));
    if (machine) {
      return machine;
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
              url: `https://${tenant.slug}.field.karry.test/enroll#7KQ2-M9XD-4RTA-0PZB`, expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
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

  /**
   * The Imports API with the worker's steps run at the next read of the job: an upload is "validating", then
   * "validated" with the report of `options.importReport`; a commit is "committing", then "committed".
   */
  /** The audit viewer's API: filters by person, site and event, pages of `limit` by sequence, export, integrity check. */
  function auditRoute(method: string, url: URL): Response | null {
    if (!url.pathname.startsWith("/api/v1/audit")) {
      return null;
    }

    const all = options.auditEntries ?? [];
    const query = url.searchParams;
    auditCalls.push({ path: url.pathname, query: Object.fromEntries(query) });
    const matching = all.filter((e) =>
      (!query.get("actor") || e.actor === query.get("actor"))
      && (!query.get("siteId") || e.siteId === query.get("siteId"))
      && (!query.get("eventType") || e.eventType === query.get("eventType"))
      && (!query.get("cursor") || Number(e.sequence) < Number(query.get("cursor"))));
    switch (true) {
      case method === "GET" && url.pathname === "/api/v1/audit/entries": {
        const limit = Number(query.get("limit") ?? 50);
        const page = matching.slice(0, limit);
        return json(200, { entries: page, nextCursor: matching.length > limit ? String(page.at(-1)?.sequence) : null });
      }
      case method === "GET" && url.pathname === "/api/v1/audit/actors": {
        const people = new Map(all.filter((e) => e.actor !== null).map((e) => [e.actor ?? "", e.actorName]));
        return json(200, { actors: [...people].sort(([a], [b]) => a.localeCompare(b)).map(([actor, name]) => ({ actor, name })) });
      }
      case method === "GET" && url.pathname === "/api/v1/audit/entries/export.csv" && options.exportTooLarge === true:
        return problem(400, "audit.export_too_large");
      case method === "GET" && url.pathname === "/api/v1/audit/entries/export.csv":
        return new Response(`\uFEFFsequence;occurred_at\r\n${matching.map((e) => `${String(e.sequence)};${e.occurredAt}`).join("\r\n")}`, {
          status: 200, headers: { "Content-Type": "text/csv; charset=utf-8" },
        });
      case method === "GET" && /^\/api\/v1\/audit\/envelopes\/[^/]+\/verification$/.test(url.pathname): {
        const found = options.envelopeVerifications?.[url.pathname.split("/")[5] ?? ""];
        return found ? json(200, found) : problem(404, "trust.envelope_not_found");
      }
      case method === "GET" && url.pathname === "/api/v1/audit/verifications/latest":
        return json(200, verification);
      case method === "POST" && url.pathname === "/api/v1/audit/verify": {
        const found = options.verifyFinds ?? { valid: true, firstBrokenSequence: null, problem: null };
        const report = { ...found, entriesChecked: all.length, headHash: null, anchorsChecked: 1, verifiedAt: "2026-10-29T09:15:00Z" };
        verification = { verification: report, verifiedBy: "user-1", verifiedByName: "Amel Admin" };
        return json(200, report);
      }
      default:
        return problem(404, "test.unknown_route");
    }
  }

  function importsRoute(method: string, url: URL): Response | null {
    if (!url.pathname.startsWith("/api/v1/imports")) {
      return null;
    }

    const [, id, part] = /^\/api\/v1\/imports(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname) ?? [];
    const report = options.importReport ?? { issues: [], rows: [{ row: 2, key: "CAR-NORD", action: "create" }] };
    const errors = report.issues.filter((i) => i.severity === "error").length;
    const count = (action: string) => report.rows.filter((r) => r.action === action).length;
    if (id === "templates" && method === "GET") {
      return new Response(new Uint8Array([0x50, 0x4b, 0x03, 0x04]), { status: 200, headers: { "Content-Type": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" } });
    }

    if (id === undefined && method === "POST") {
      const job: ImportJob = {
        id: `0192f0c1-0000-7000-8000-00000000900${String(imports.length + 1)}`, kind: url.searchParams.get("kind") ?? "", fileName: url.searchParams.get("fileName") ?? "",
        size: 4, sha256: "b".repeat(64), status: "validating", rows: 0, errors: 0, warnings: 0, created: 0, updated: 0, unchanged: 0, scanner: null,
        uploadedAt: "2026-10-28T08:00:00Z", changedAt: "2026-10-28T08:00:00Z", fileKept: true,
      };
      imports.unshift(job);
      return json(202, job);
    }

    if (id === undefined) {
      return json(200, { items: imports, nextCursor: null });
    }

    const index = imports.findIndex((j) => j.id === id);
    const job = imports[index];
    if (!job) {
      return problem(404, "imports.job_not_found");
    }

    const save = (next: Partial<ImportJob>) => {
      importSteps += 1;
      imports[index] = { ...job, ...next, changedAt: `2026-10-28T08:00:${String(importSteps).padStart(2, "0")}Z` };
      return json(200, imports[index]);
    };
    switch (true) {
      case part === undefined && job.status === "validating":
        return save({
          status: "validated", rows: report.rows.length + errors, errors,
          warnings: report.issues.length - errors, created: 0, updated: 0, unchanged: 0,
        });
      case part === undefined && job.status === "committing":
        return save({ status: "committed", created: count("create"), updated: count("update"), unchanged: count("unchanged") });
      case part === undefined:
        return json(200, job);
      case part === "issues":
        return json(200, { items: report.issues, nextCursor: null });
      case part === "rows":
        return json(200, { items: errors > 0 ? [] : report.rows, nextCursor: null });
      case part === "commit":
        return errors > 0 ? problem(422, "imports.job_has_errors") : save({ status: "committing" });
      case part === "discard":
        return save({ status: "discarded" });
      default:
        return problem(404, "test.unknown_route");
    }
  }

  function organizationRoute(method: string, url: URL, body: unknown, ifMatch: string | null): Response | null {
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

    // As the API: every saved change is a new version, and a save for an older one is refused (H-C1).
    const save = (next: Site) => {
      const version = `"${String(Number(site.version.replaceAll('"', "")) + 1)}"`;
      sites[index] = { ...next, version, changedAt: `2026-10-26T09:00:${String(index).padStart(2, "0")}Z` };
      return json(200, sites[index]);
    };
    if (method === "PUT" && part === undefined && ifMatch !== null && ifMatch !== site.version) {
      return problem(409, "request.version_conflict");
    }
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

  function materialsRoute(method: string, url: URL, body: unknown, ifMatch: string | null): Response | null {
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

    // As the API: every saved change is a new version (densities included), and a save for an older one is refused (H-C1).
    const save = (next: Material) => {
      materials[index] = { ...next, version: nextVersion(found.version), changedAt: `2026-10-26T09:00:${String(index).padStart(2, "0")}Z` };
      return json(200, materials[index]);
    };
    if (method === "PUT" && part === undefined && ifMatch !== null && ifMatch !== found.version) {
      return problem(409, "request.version_conflict");
    }

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

  function assetsRoute(method: string, url: URL, body: unknown, ifMatch: string | null): Response | null {
    const match = /^\/api\/v1\/assets(?:\/([^/]+))?(?:\/([^/]+))?(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const [, id, part, meterId, sub] = match;
    if (id === "types") {
      return json(200, { items: [{ code: "excavator", defaultMeters: ["hours"], components: [] }, { code: "haul-truck", defaultMeters: ["hours", "km"], components: [] }] });
    }

    if (id === "labels" && part === "sheet") {
      return new Response(new Uint8Array([0x25, 0x50, 0x44, 0x46]), { status: 200, headers: { "Content-Type": "application/pdf" } });
    }

    if (!id) {
      if (method === "GET") {
        const q = (url.searchParams.get("q") ?? "").toLowerCase();
        const status = url.searchParams.get("status");
        const found = assets.filter((a) =>
          (status ? a.status === status : a.status !== "disposed")
          && [a.code, a.name, a.plate ?? ""].some((text) => text.toLowerCase().includes(q))
          && (url.searchParams.get("siteId") ?? a.siteId) === a.siteId
          && (url.searchParams.get("type") ?? a.type) === a.type);
        return json(200, { items: found, nextCursor: null });
      }

      const input = body as AssetInput;
      const created = asset({
        id: `0192f0c1-0000-7000-8000-${String(assets.length + 1).padStart(12, "0")}`, code: (input.code ?? "").toUpperCase(), name: input.name ?? "",
        type: input.type ?? "", make: input.make ?? null, model: input.model ?? null, serialNumber: input.serialNumber ?? null, plate: input.plate ?? null,
        year: input.year ?? null, acquiredOn: input.acquiredOn ?? null, siteId: input.siteId ?? "",
        sites: [{ siteId: input.siteId ?? "", from: "2026-10-27T08:00:00Z" }],
      });
      assets.push(created);
      return json(201, created);
    }

    const index = assets.findIndex((a) => a.id === id);
    const found = assets[index];
    if (!found) {
      return problem(404, "assets.asset_not_found");
    }

    // As the API: every saved change is a new version (moves, status, photo, label included), and a save of the
    // details for an older one is refused (H-C1).
    const save = (next: Asset) => {
      assets[index] = { ...next, version: nextVersion(found.version), changedAt: `2026-10-27T09:00:${String(index).padStart(2, "0")}Z` };
      return json(200, assets[index]);
    };
    if (method === "PUT" && part === undefined && ifMatch !== null && ifMatch !== found.version) {
      return problem(409, "request.version_conflict");
    }

    const list = meters[id] ?? [];
    const meter = list.find((m) => m.id === meterId);
    switch (true) {
      case part === undefined && method === "GET":
        return json(200, found);
      case part === undefined && method === "PUT":
        return save({ ...found, ...(body as object) });
      case part === "move": {
        const siteId = (body as { siteId: string }).siteId;
        return save({ ...found, siteId, sites: [...found.sites, { siteId, from: "2026-10-27T09:00:00Z" }] });
      }
      case part === "status":
        return save({ ...found, status: (body as { status: string }).status });
      case part === "label":
        return save({ ...found, label: "KRY1:NEWLABELTOKENAAAAAAAAAAA" });
      case part === "photo" && method === "PUT":
        return save({ ...found, photo: { sha256: "a".repeat(64), contentType: "image/png", bytes: 4 } });
      case part === "photo" && method === "DELETE":
        return save({ ...found, photo: null });
      case part === "photo" && method === "GET":
        return new Response(new Uint8Array([0x89, 0x50, 0x4e, 0x47]), { status: 200, headers: { "Content-Type": "image/png" } });
      case part === "meters" && meterId === undefined && method === "GET":
        return json(200, { items: list });
      case part === "meters" && meterId === undefined && method === "POST": {
        const input = body as { kind: string; initialReading: string };
        const added = meterOf({ id: `meter-${String(list.length + 1)}`, kind: input.kind, lastValue: Number(input.initialReading), initialReading: Number(input.initialReading) });
        meters[id] = [...list, added];
        return json(201, added);
      }
      case meter !== undefined && sub === "readings" && method === "GET":
        return json(200, { items: readings[meter.id] ?? [], nextCursor: null });
      case meter !== undefined && sub === "readings" && method === "POST": {
        const value = Number((body as { value: string }).value);
        if (value < Number(meter.lastValue)) {
          return problem(400, "assets.reading_decreased");
        }

        const reading: Reading = { id: `reading-${String(Date.now())}`, value, readAt: "2026-10-27T10:00:00Z", recordedAt: "2026-10-27T10:00:00Z", recordedBy: "admin-1" };
        readings[meter.id] = [reading, ...(readings[meter.id] ?? [])];
        meters[id] = list.map((m) => (m.id === meter.id ? { ...m, lastValue: value, lastReadAt: reading.readAt } : m));
        return json(201, reading);
      }
      case meter !== undefined && sub === "status": {
        const next = { ...meter, status: (body as { status: string }).status };
        meters[id] = list.map((m) => (m.id === meter.id ? next : m));
        return json(200, next);
      }
      default:
        return problem(404, "test.unknown_route");
    }
  }

  const client: ApiClient = createApiClient({ baseUrl: "http://api.test", getAccessToken: () => "test-token", fetch });
  return {
    client,
    sent,
    /** Calls of the audit API with their query (filters, cursor). */
    auditCalls,
    /** Every path asked, in order (reads included). */
    requested,
    /** Every call with its `Karry-Support-Session` header (null without one). */
    supportCalls,
    /** The company ends a session (Settings › Support access): its next call is refused. */
    endSupport: (id: string) => {
      const index = supportSessions.findIndex((s) => s.id === id);
      const found = supportSessions[index];
      if (found) {
        supportSessions[index] = { ...found, active: false, endedAt: new Date().toISOString() };
      }
    },
    /** Another person saves the site meanwhile (a new version): the open form is now out of date (H-C1). */
    changeSiteElsewhere: (id: string, change: Partial<Site>) => {
      const index = sites.findIndex((s) => s.id === id);
      const found = sites[index];
      if (found) {
        sites[index] = { ...found, ...change, version: `"${String(Number(found.version.replaceAll('"', "")) + 1)}"` };
      }
    },
    /** Another person saves the material meanwhile (a new version): the open form is now out of date (H-C1). */
    changeMaterialElsewhere: (id: string, change: Partial<Material>) => {
      const index = materials.findIndex((m) => m.id === id);
      const found = materials[index];
      if (found) {
        materials[index] = { ...found, ...change, version: nextVersion(found.version) };
      }
    },
    /** Another operator changes the company's subscription meanwhile (a new version): the open terms form is now out of date (H-C1). */
    changeSubscriptionElsewhere: (tenantId: string, change: Partial<Subscription>) => {
      const found = tenantSubscriptions[tenantId] ?? subscriptionOf();
      tenantSubscriptions[tenantId] = { ...found, ...change, version: nextVersion(found.version) };
    },
    /** Another administrator saves the company settings meanwhile (a new version): the open form is now out of date (H-C1). */
    changeTenantElsewhere: (change: Partial<Tenant>) => {
      tenant = { ...tenant, ...change, version: nextVersion(tenant.version) };
    },
    /** Another administrator changes the person's roles meanwhile (a new version): the open form is now out of date (H-C1). */
    changeUserElsewhere: (id: string, change: Partial<User>) => {
      const index = users.findIndex((u) => u.id === id);
      const found = users[index];
      if (found) {
        users[index] = { ...found, ...change, version: nextVersion(found.version) };
      }
    },
    /** Another administrator saves the role meanwhile (a new version): the open form is now out of date (H-C1). */
    changeRoleElsewhere: (code: string, change: Partial<Role>) => {
      const index = roles.findIndex((r) => r.code === code);
      const found = roles[index];
      if (found) {
        roles[index] = { ...found, ...change, version: nextVersion(found.version) };
      }
    },
    /** Another person saves the machine meanwhile (a new version): the open form is now out of date (H-C1). */
    changeAssetElsewhere: (id: string, change: Partial<Asset>) => {
      const index = assets.findIndex((a) => a.id === id);
      const found = assets[index];
      if (found) {
        assets[index] = { ...found, ...change, version: nextVersion(found.version) };
      }
    },
    /** Changes sent (a conversion is a POST but stores nothing). */
    writes: () => sent.filter((s) => s.method !== "GET" && !s.path.endsWith("/conversions")),
  };
}

/** An active tablet of Carrière Nord as the API lists it. */
export function device(overrides: Partial<Device> = {}): Device {
  return {
    id: "0192f0c1-0000-7000-8000-00000000d001", name: "Tablette pesage", model: "Galaxy Tab A9", platform: "Android 14", appVersion: "1.0.0",
    siteId: site().id, status: "active", enrolledAt: "2026-10-27T08:00:00Z", lastSeenAt: "2026-10-28T07:55:00Z", revokedAt: null, ...overrides,
  };
}

export function user(overrides: Partial<User> = {}): User {
  return {
    id: "0192f0c1-0000-7000-8000-000000000001", kind: "office", status: "active", firstName: "Sami", lastName: "Gharbi",
    email: "sami@example.test", phone: null, employeeId: null, roles: [{ role: "commercial", siteId: null }], requiresMfa: false,
    createdAt: "2026-10-24T08:00:00Z", deactivatedAt: null, pinSet: false, version: '"1"', ...overrides,
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
    createdAt: "2026-10-26T08:00:00Z", changedAt: "2026-10-26T08:00:00Z", archivedAt: null, version: '"1"',
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
    status: "active", createdAt: "2026-10-26T08:00:00Z", changedAt: "2026-10-26T08:00:00Z", archivedAt: null, version: '"1"', ...overrides,
  };
}

/** The next record version, as PostgreSQL's xmin moves on with every save. */
function nextVersion(version: string): string {
  return `"${String(Number(version.replaceAll('"', "")) + 1)}"`;
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

/** A machine as the API answers it: excavator PEL-01 on Carrière Nord. */
export function asset(overrides: Partial<Asset> = {}): Asset {
  const siteId = overrides.siteId ?? "0192f0c1-0000-7000-8000-000000005001";
  return {
    id: "0192f0c1-0000-7000-8000-000000007001", code: "PEL-01", name: "Pelle Cat 336", type: "excavator", make: "Caterpillar", model: "336",
    serialNumber: "CAT0336X1234", plate: null, year: 2021, acquiredOn: "2021-03-15", siteId, status: "active",
    label: "KRY1:ABCDEFGHIJKLMNOPQRSTUVWXYZ", photo: null, sites: [{ siteId, from: "2026-01-05T08:00:00Z" }],
    createdAt: "2026-10-27T08:00:00Z", changedAt: "2026-10-27T08:00:00Z", disposedAt: null, version: '"1"', ...overrides,
  };
}

/** An hour meter at 4,250.5 h, working. */
export function meterOf(overrides: Partial<Meter> = {}): Meter {
  return {
    id: "0192f0c1-0000-7000-8000-000000008001", kind: "hours", unit: "h", initialReading: 0, installedOn: "2021-03-15", status: "ok", offset: 0,
    lastValue: 4250.5, lastReadAt: "2026-10-26T16:00:00Z", createdAt: "2026-10-27T08:00:00Z", ...overrides,
  };
}

/** An event as an audit entry carries it (the schema types it as an opaque JSON object). */
export function recordedEvent(value: Record<string, unknown>): AuditEntry["event"] {
  return value as AuditEntry["event"];
}

/** An audit entry as the API answers it: Ali changed machine PEL-01's name on Carrière Nord. */
export function auditEntry(overrides: Partial<AuditEntry> = {}): AuditEntry {
  return {
    sequence: 1, eventId: "0192f0c1-0000-7000-8000-00000000a001", eventType: "assets.asset_changed.v1", occurredAt: "2026-10-28T09:30:00Z",
    actor: "user-ali", actorName: "Ali Ben Salah", entityType: "asset", entityId: "0192f0c1-0000-7000-8000-000000007001",
    siteId: "0192f0c1-0000-7000-8000-000000005001", event: recordedEvent({ assetId: "0192f0c1-0000-7000-8000-000000007001", before: { name: "Pelle" }, after: { name: "Pelle Cat 336" } }),
    changes: [{ path: "name", before: "Pelle", after: "Pelle Cat 336" }], ...overrides,
  };
}

/** A company of the operator console: Carrière Nord on a Professional trial, 2 sites, 3 machines, 4 office users. */
export function operatorTenant(overrides: Partial<OperatorTenant> = {}): OperatorTenant {
  return {
    id: "0192f0c1-0000-7000-8000-0000000c0001", slug: "carriere-nord", name: "Carrière Nord", createdAt: "2026-10-01T08:00:00Z",
    planCode: "professional", planVersion: 1, status: "trial", trialEndsAt: "2026-10-31T08:00:00Z", mrr: 0, mrrCurrency: "EUR",
    sites: 2, assets: 3, officeUsers: 4, lastActivity: "2026-10-28T16:00:00Z", complete: true, ...overrides,
  };
}

/** A company's subscription as the API returns it (default: a professional trial on one site). */
export function subscriptionOf(overrides: Partial<Subscription> = {}): Subscription {
  return {
    id: "01970000-0000-7000-8000-0000000000bb", status: "trial", statusSince: "2026-10-01T08:00:00Z", trialEndsAt: "2026-10-31T08:00:00Z",
    unpaidSince: null, planCode: "professional", planVersion: 1, period: "monthly", sites: 1, extraAssets: 0, addOns: {}, extraFeatures: [], limitOverrides: {},
    version: '"1"', ...overrides,
  };
}

/** A support session as the API lists it (default: a running session opened with the company's code). */
export function supportSession(overrides: Partial<SupportSession> = {}): SupportSession {
  return {
    id: "0192f0c1-0000-7000-8000-00000000d001", tenantId: "0192f0c1-0000-7000-8000-0000000000a1", mode: "consent", operatorName: "Anna Support",
    reason: null, startedAt: "2026-10-29T10:00:00Z", endsAt: "2026-10-29T11:00:00Z", endedAt: null, active: true, ...overrides,
  };
}

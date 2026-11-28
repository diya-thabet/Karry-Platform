import { createApiClient, type ApiClient } from "@karry/api-client";
import { nextVersion, subscriptionOf } from "./fake/factories";
import { answer, problem, type Ctx } from "./fake/http";
import { assetsRoutes } from "./fake/routes/assets";
import { auditRoutes } from "./fake/routes/audit";
import { billingRoutes } from "./fake/routes/billing";
import { fieldopsRoutes } from "./fake/routes/fieldops";
import { identityRoutes } from "./fake/routes/identity";
import { importsRoutes } from "./fake/routes/imports";
import { integrityRoutes } from "./fake/routes/integrity";
import { materialsRoutes } from "./fake/routes/materials";
import { notificationsRoutes } from "./fake/routes/notifications";
import { organizationRoutes } from "./fake/routes/organization";
import { salesRoutes } from "./fake/routes/sales";
import { sessionRoutes } from "./fake/routes/session";
import { tenancyRoutes } from "./fake/routes/tenancy";
import { verificationRoutes } from "./fake/routes/verification";
import { weighbridgeRoutes } from "./fake/routes/weighbridge";
import { createState } from "./fake/state";
import type { Asset, IntegrityRule, Material, ReviewItem, Role, ShiftEntry, Site, Subscription, Tenant, User, FakeApiOptions } from "./fake/types";

export * from "./fake/factories";
export type { FakeApiOptions, Sent } from "./fake/types";

/**
 * The Karry API as the portal's tests meet it: one route module per API module (`fake/routes`), over one state per
 * test (`fake/state`). A call is answered in this order: a support visit's checks, what every page reads at sign-in,
 * then (recorded in `sent`) the switches of the test (a failure, refused writes, a step-up), then the modules' routes.
 */
export function fakeApi(options: FakeApiOptions) {
  const s = createState(options);
  const { roles, users, sent, sites, integrityRules, assets, reviewItems, entries, queueCalls, decisions, settlements, integrityFlags, materials, supportSessions, supportCalls, auditCalls, entryCalls, shiftReportCalls, requested, tenantSubscriptions } = s;
  const early = sessionRoutes(s, options);
  // Identity last: its routes answer any other path as unknown.
  const routes = [
    ...tenancyRoutes(s, options), ...billingRoutes(s), ...organizationRoutes(s, options), ...verificationRoutes(s), ...fieldopsRoutes(s),
    ...notificationsRoutes(s, options), ...integrityRoutes(s), ...materialsRoutes(s), ...salesRoutes(s, options), ...weighbridgeRoutes(s),
    ...importsRoutes(s, options), ...auditRoutes(s, options), ...assetsRoutes(s), ...identityRoutes(s, options),
  ];

  /** A support visit (ADR-0029 §4): the named session must be running; then reads only. */
  const supportVisitRefusal = (request: Request, url: URL): Response | null => {
    const named = request.headers.get("Karry-Support-Session");
    supportCalls.push({ method: request.method, path: url.pathname, header: named });
    if (named === null) {
      return null;
    }

    const visited = supportSessions.find((session) => named === `${session.tenantId}.${session.id}`);
    if (!visited?.active) {
      return problem(403, "support.session_invalid");
    }

    return ["GET", "HEAD"].includes(request.method) ? null : problem(403, "support.read_only");
  };

  /** The test's switches: every call fails, writes are refused, or a write needs a step-up. */
  const testSwitch = (request: Request): Response | null => {
    if (options.failWith) {
      return problem(options.failWith.status, options.failWith.code);
    }

    if (request.method !== "GET" && options.refuseWrites) {
      return problem(options.refuseWrites.status, options.refuseWrites.code);
    }

    return request.method !== "GET" && options.needsStepUp?.() === true ? problem(401, "auth.step_up_required") : null;
  };

  const fetch = async (input: Request | URL | string, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const url = new URL(request.url);
    requested.push(url.pathname);
    const refused = supportVisitRefusal(request, url);
    if (refused) {
      return refused;
    }

    const path = url.pathname.replace("/api/v1/identity", "");
    const ctx: Ctx = { request, url, method: request.method, path, body: null, ifMatch: request.headers.get("If-Match") };
    const signIn = answer(early, ctx);
    if (signIn) {
      return signIn;
    }

    const isJson = request.headers.get("Content-Type")?.includes("json") ?? false;
    ctx.body = ["POST", "PUT"].includes(request.method) && isJson ? await request.clone().json().catch(() => null) : null;
    sent.push({ method: request.method, path, body: ctx.body });
    return testSwitch(request) ?? answer(routes, ctx) ?? problem(404, "test.unknown_route");
  };

  const client: ApiClient = createApiClient({ baseUrl: "http://api.test", getAccessToken: () => "test-token", fetch });
  return {
    client,
    sent,
    /** Calls of the audit API with their query (filters, cursor). */
    auditCalls,
    /** Searches of shift entries with their query (the history's period, the export's pages). */
    entryCalls,
    /** Shift reports issued, with their query (site, date, shift, language). */
    shiftReportCalls,
    /** Every path asked, in order (reads included). */
    requested,
    /** Every ask of the review queue with its query and `If-None-Match`. */
    queueCalls,
    /** Every decision on a review item: approve, reject, correct or second-approve, with its `If-Match` and body. */
    decisions,
    /** Every step of a gap settlement: settle, settlement/approve or settlement/decline, with its `If-Match` and body. */
    settlements,
    /** Someone else acts on the flag meanwhile: the version the page holds is out of date. */
    changeFlagElsewhere: (id: string) => {
      const index = integrityFlags.findIndex((f) => f.id === id);
      const found = integrityFlags[index];
      if (found) {
        integrityFlags[index] = { ...found, version: nextVersion(found.version ?? '"1"') };
      }
    },
    /** Someone else decides on the item meanwhile: the version the open panel holds is out of date. */
    changeReviewItemElsewhere: (id: string) => {
      const index = reviewItems.findIndex((i) => i.id === id);
      const found = reviewItems[index];
      if (found) {
        reviewItems[index] = { ...found, version: nextVersion(found.version) };
      }
    },
    /** An entry ends on a tablet and syncs: its review item joins the queue. */
    addReviewItem: (item: ReviewItem, entry?: ShiftEntry) => {
      reviewItems.push(item);
      if (entry) {
        entries.push(entry);
      }
    },
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
    /** Another administrator saves the rule meanwhile (a new version): the open form is now out of date (H-C1). */
    changeRuleElsewhere: (code: string, change: Partial<IntegrityRule>) => {
      const index = integrityRules.findIndex((r) => r.rule === code);
      const found = integrityRules[index];
      if (found) {
        integrityRules[index] = { ...found, ...change, version: nextVersion(found.version) };
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
      s.tenant = { ...s.tenant, ...change, version: nextVersion(s.tenant.version) };
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

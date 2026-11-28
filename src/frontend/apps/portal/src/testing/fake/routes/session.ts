import { json, type Route } from "../http";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** Answered before a call is recorded or refused: what every page reads at sign-in. */
export function sessionRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { sent } = s;
  const entitlements: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/billing/entitlements") {
      return json(200, {
        planCode: "professional", planVersion: 1, status: options.status ?? "trial", trialEndsAt: "2026-11-24T08:00:00Z",
        features: options.features ?? ["field-operations", "maintenance"], limits: { "office-users": 15 },
      });
    }
    return null;
  };

  const currentTenant: Route = ({ request, url }) => {
    if (url.pathname === "/api/v1/tenancy/current" && request.method === "GET") {
      return json(200, s.tenant);
    }
    return null;
  };

  const flags: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/flags") {
      return json(200, { flags: options.flags ?? {} });
    }
    return null;
  };

  const usage: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/billing/usage") {
      sent.push({ method: "GET", path: url.pathname, body: null });
      return json(200, { items: options.usage ?? [{ metric: "office-users", usage: 3, limit: 15, ceiling: 17, state: "within" }], snapshotOn: "2026-10-25" });
    }
    return null;
  };

  const subscription: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/billing/subscription") {
      return json(200, {
        id: "01970000-0000-7000-8000-0000000000aa", status: "trial", statusSince: "2026-10-25T08:00:00Z", trialEndsAt: "2026-11-24T08:00:00Z",
        unpaidSince: null, planCode: "professional", planVersion: 1, period: "monthly", sites: 1, extraAssets: 0, addOns: {}, extraFeatures: [], limitOverrides: {},
      });
    }
    return null;
  };

  return [entitlements, currentTenant, flags, usage, subscription];
}

import { json, problem, type Route } from "../http";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** The audit viewer's API: filters, pages, export, envelope verification, integrity check. */
export function auditRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { auditCalls } = s;
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
        return json(200, s.verification);
      case method === "POST" && url.pathname === "/api/v1/audit/verify": {
        const found = options.verifyFinds ?? { valid: true, firstBrokenSequence: null, problem: null };
        const report = { ...found, entriesChecked: all.length, headHash: null, anchorsChecked: 1, verifiedAt: "2026-10-29T09:15:00Z" };
        s.verification = { verification: report, verifiedBy: "user-1", verifiedByName: "Amel Admin" };
        return json(200, report);
      }
      default:
        return problem(404, "test.unknown_route");
    }
  }

  return [(ctx) => auditRoute(ctx.method, ctx.url)];
}

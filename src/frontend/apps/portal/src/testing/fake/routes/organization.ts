import type { NonWorkingDay, ShiftPattern, Site, SiteInput } from "../types";
import { json, problem, type Route } from "../http";
import { siteFrom } from "../factories";
import type { FakeState } from "../state";
import type { FakeApiOptions } from "../types";

/** Sites with their shift patterns, non-working days and shift instances. */
export function organizationRoutes(s: FakeState, options: FakeApiOptions): Route[] {
  const { sites, patterns, days } = s;
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

  return [(ctx) => organizationRoute(ctx.method, ctx.url, ctx.body, ctx.ifMatch)];
}

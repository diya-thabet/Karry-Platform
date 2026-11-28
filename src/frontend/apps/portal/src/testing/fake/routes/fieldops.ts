import type { Checklist, PublishChecklist, ShiftEntry } from "../types";
import { json, problem, type Route } from "../http";
import type { FakeState } from "../state";

/** Shift entries and their photos, shift reports, safety holds and checklist templates. */
export function fieldopsRoutes(s: FakeState): Route[] {
  const { checklists, holds, entries, entryCalls, shiftReportCalls } = s;
  const shiftEntries: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/fieldops/entries") {
      entryCalls.push({ query: Object.fromEntries(url.searchParams) });
      const ids = url.searchParams.getAll("ids");
      const site = url.searchParams.get("siteId");
      const day = url.searchParams.get("shiftDate");
      const asset = url.searchParams.get("assetId");
      const operator = url.searchParams.get("operatorId");
      // The period of shift dates, both ends included (the history and its export).
      const from = url.searchParams.get("from");
      const to = url.searchParams.get("to");
      const dated = (entry: ShiftEntry) => (!from || (entry.shift?.date ?? "") >= from) && (!to || (entry.shift?.date ?? "") <= to);
      return json(200, {
        items: entries.filter((entry) => (ids.length === 0 || ids.includes(entry.id)) && (!site || entry.siteId === site) && (!day || entry.shift?.date === day)
          && (!asset || entry.assetId === asset) && (!operator || entry.operatorId === operator) && dated(entry)),
        nextCursor: null,
      });
    }

    const photoMatch = /^\/api\/v1\/fieldops\/entries\/([^/]+)\/photos\/([^/]+)$/.exec(url.pathname);
    if (request.method === "GET" && photoMatch) {
      const entry = entries.find((e) => e.id === photoMatch[1]);
      const reference = `sha256:${photoMatch[2] ?? ""}`;
      return entry && (entry.startPhoto === reference || entry.endPhoto === reference)
        ? new Response(new Blob([new Uint8Array([0xff, 0xd8, 0xff, 0xd9])], { type: "image/jpeg" }), { status: 200, headers: { "Content-Type": "image/jpeg" } })
        : problem(404, "fieldops.photo_not_found");
    }

    const entryMatch = /^\/api\/v1\/fieldops\/entries\/([^/]+)$/.exec(url.pathname);
    if (request.method === "GET" && entryMatch) {
      // The entry as it stands, its versions, and the entry before it on the same meter (ADR-0038 §2).
      const entry = entries.find((e) => e.id === entryMatch[1]);
      if (!entry) {
        return problem(404, "fieldops.entry_not_found");
      }

      const previous = entries
        .filter((e) => e.meterId === entry.meterId && e.startedAt < entry.startedAt)
        .sort((a, b) => b.startedAt.localeCompare(a.startedAt))[0] ?? null;
      return json(200, { entry, versions: [entry], previousOnMeter: previous });
    }
    return null;
  };

  const shiftReports: Route = ({ request, url }) => {
    if (request.method === "GET" && url.pathname === "/api/v1/documents/shift-reports") {
      shiftReportCalls.push(Object.fromEntries(url.searchParams));
      return new Response(new Uint8Array([0x25, 0x50, 0x44, 0x46]), {
        status: 200, headers: { "Content-Type": "application/pdf", "X-Karry-Envelope": "0192f0c1-0000-7000-8000-00000000d001" },
      });
    }
    return null;
  };

  // Safety holds: the list (open by default, released, all; by site) and the release of one (ADR-0035 §5).
  function holdsRoute(method: string, url: URL, body: unknown): Response | null {
    const match = /^\/api\/v1\/fieldops\/holds(?:\/([^/]+)\/release)?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const id = match[1];
    if (!id && method === "GET") {
      const status = url.searchParams.get("status") ?? "open";
      const site = url.searchParams.get("siteId");
      return json(200, { items: holds.filter((h) => (status === "all" || (status === "open") === (h.release === null)) && (!site || h.siteId === site)) });
    }

    const index = holds.findIndex((h) => h.id === id);
    const found = holds[index];
    if (!found) {
      return problem(404, "fieldops.hold_not_found");
    }

    if (found.release) {
      return problem(409, "fieldops.hold_released");
    }

    const release = { holdId: found.id, assetId: found.assetId, releasedAt: "2026-11-19T10:00:00Z", releasedBy: "admin-1", reason: (body as { reason: string }).reason };
    holds[index] = { ...found, release };
    return json(200, release);
  }



  // Checklist templates: the newest per machine type, every version of one type, publish the next (ADR-0035 §2).
  function checklistsRoute(method: string, url: URL, body: unknown): Response | null {
    const match = /^\/api\/v1\/fieldops\/checklists(?:\/([^/]+)\/versions)?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const type = match[1];
    const newestFirst = [...checklists].sort((a, b) => Number(b.version) - Number(a.version));
    if (!type) {
      const latest = newestFirst.filter((c, index) => newestFirst.findIndex((n) => n.assetType === c.assetType) === index);
      return json(200, { items: latest.sort((a, b) => a.assetType.localeCompare(b.assetType)) });
    }

    const versions = newestFirst.filter((c) => c.assetType === type);
    if (method === "GET") {
      return versions.length === 0 ? problem(404, "fieldops.checklist_not_found") : json(200, { items: versions });
    }

    const input = body as PublishChecklist;
    const published: Checklist = {
      id: `0192f0c1-0000-7000-8000-${String(9000 + checklists.length).padStart(12, "0")}`, assetType: type, version: Number(versions[0]?.version ?? 0) + 1,
      required: input.required, publishedAt: "2026-11-19T09:00:00Z",
      items: (input.items ?? []).map((item) => ({ code: item.code ?? "", texts: item.texts ?? {}, critical: item.critical, photoOnNotOk: item.photoOnNotOk })),
    };
    checklists.push(published);
    return json(201, published);
  }

  return [shiftEntries, shiftReports, (ctx) => holdsRoute(ctx.method, ctx.url, ctx.body), (ctx) => checklistsRoute(ctx.method, ctx.url, ctx.body)];
}

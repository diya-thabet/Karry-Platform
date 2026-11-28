import type { Asset, AssetInput, Reading } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { asset, meterOf } from "../factories";
import type { FakeState } from "../state";

/** Machines with their meters, readings and labels. */
export function assetsRoutes(s: FakeState): Route[] {
  const { assets, meters, readings, meterEvents } = s;
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
      case meter !== undefined && sub === "evidence" && method === "POST":
        return json(200, { photo: `sha256:${"c".repeat(64)}` });
      case meter !== undefined && sub === "events" && method === "GET":
        return json(200, { items: meterEvents[meter.id] ?? [] });
      case meter !== undefined && sub === "events" && method === "POST": {
        const input = body as { kind: string; occurredAt: string; valueBefore: string | null; valueAfter: string | null; reason: string; photos: string[] };
        const saved = { id: `event-${String(Date.now())}`, ...input, recordedAt: "2026-11-19T10:00:00Z", recordedBy: "admin-1" };
        meterEvents[meter.id] = [...(meterEvents[meter.id] ?? []), saved];
        return json(201, saved);
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

  return [(ctx) => assetsRoute(ctx.method, ctx.url, ctx.body, ctx.ifMatch)];
}

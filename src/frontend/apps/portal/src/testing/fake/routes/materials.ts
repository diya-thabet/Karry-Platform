import type { ConversionInput, DensityInput, Material, MaterialInput } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { convert, material, versionFrom } from "../factories";
import type { FakeState } from "../state";

/** Materials with their density versions and conversions. */
export function materialsRoutes(s: FakeState): Route[] {
  const { materials } = s;
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

  return [(ctx) => materialsRoute(ctx.method, ctx.url, ctx.body, ctx.ifMatch)];
}

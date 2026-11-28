import type { HaulerInput, Truck, TruckInput } from "../types";
import { json, problem, nextVersion, type Route } from "../http";
import { hauler, truck } from "../factories";
import type { FakeState } from "../state";

/** Trucks and haulers (P3.2). */
export function weighbridgeRoutes(s: FakeState): Route[] {
  const { trucks, haulers } = s;
  function weighbridgeRoute(method: string, url: URL, body: unknown, ifMatch: string | null): Response | null {
    const match = /^\/api\/v1\/weighbridge\/(trucks|haulers)(?:\/([^/]+))?(?:\/([^/]+))?$/.exec(url.pathname);
    if (!match) {
      return null;
    }

    const [, kind, id, action] = match;
    const key = (text: string) => text.toUpperCase().replace(/[^A-Z0-9]/g, "");
    if (kind === "haulers") {
      if (!id && method === "GET") {
        const all = url.searchParams.get("includeArchived") === "true";
        return json(200, { items: haulers.filter((h) => all || h.status === "active") });
      }

      const input = body as HaulerInput;
      if (haulers.some((h) => h.id !== id && key(h.name) === key(input.name ?? ""))) {
        return problem(409, "weighbridge.hauler_name_taken");
      }

      if (!id) {
        const created = hauler({ id: `0192f0c1-0000-7000-8000-${String(9500 + haulers.length + 1).padStart(12, "0")}`, name: input.name ?? "", phone: input.phone ?? null });
        haulers.push(created);
        return json(201, created);
      }

      const index = haulers.findIndex((h) => h.id === id);
      const found = haulers[index];
      if (!found) {
        return problem(404, "weighbridge.hauler_not_found");
      }

      haulers[index] = { ...found, name: input.name ?? "", phone: input.phone ?? null, status: input.status ?? "active", version: nextVersion(found.version) };
      return json(200, haulers[index]);
    }

    if (!id && method === "GET") {
      const plate = key(url.searchParams.get("plate") ?? "");
      const status = url.searchParams.get("status");
      return json(200, { items: trucks.filter((t) => (!status || t.status === status) && t.plateKey.includes(plate)) });
    }

    if (!id || id === "unknown") {
      const input = body as TruckInput;
      const plate = (input.plate ?? "").trim().toUpperCase();
      if (key(plate).length < 2) {
        return problem(400, "weighbridge.truck_plate_invalid");
      }

      if (trucks.some((t) => t.plateKey === key(plate))) {
        return problem(409, "weighbridge.truck_plate_taken");
      }

      const created = truck({
        id: `0192f0c1-0000-7000-8000-${String(9600 + trucks.length + 1).padStart(12, "0")}`, plate, plateKey: key(plate), haulerId: input.haulerId ?? null,
        tare: input.tare?.kg && input.tare.on ? { kg: Number(input.tare.kg), on: input.tare.on, method: input.tare.method ?? "weighed" } : null,
        maxPayloadKg: input.maxPayloadKg === null ? null : Number(input.maxPayloadKg), axles: input.axles ?? null,
        status: id === "unknown" ? "unverified" : "active",
      });
      trucks.push(created);
      return json(201, created);
    }

    const index = trucks.findIndex((t) => t.id === id);
    const found = trucks[index];
    if (!found) {
      return problem(404, "weighbridge.truck_not_found");
    }

    const save = (next: Truck) => {
      trucks[index] = { ...next, version: nextVersion(found.version) };
      return json(200, trucks[index]);
    };
    if (method === "GET") {
      return json(200, found);
    }

    if (action === "verify") {
      return found.status === "unverified" ? save({ ...found, status: "active" }) : json(200, found);
    }

    if (action === "archive" || action === "restore") {
      return save({ ...found, status: action === "archive" ? "archived" : "active" });
    }

    if (ifMatch !== null && ifMatch !== found.version) {
      return problem(409, "request.version_conflict");
    }

    const input = body as TruckInput;
    return save({
      ...found, haulerId: input.haulerId ?? null,
      tare: input.tare?.kg && input.tare.on ? { kg: Number(input.tare.kg), on: input.tare.on, method: input.tare.method ?? "weighed" } : null,
      maxPayloadKg: input.maxPayloadKg === null ? null : Number(input.maxPayloadKg), axles: input.axles ?? null,
    });
  }

  return [(ctx) => weighbridgeRoute(ctx.method, ctx.url, ctx.body, ctx.ifMatch)];
}

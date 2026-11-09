import "fake-indexeddb/auto";
import { fromBase64Url, generateDeviceKey, importPinKey, publicJwk, utf8 } from "@karry/crypto";
import { enqueue, lastRefresh, openOfflineDb, type ChangeSet, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";
import { changesPath, pullChanges, snapshotEveryMs } from "./sync";

const site = "0192f0a1-0000-7000-8000-000000000051";

function changeSet(full: boolean, cursor: string, serverTime: string, assets: { id: string; code: string }[] = []): ChangeSet {
  return {
    full, cursor, serverTime,
    changes: {
      sites: { upserts: [{ id: site, code: "NORD", name: "Carrière Nord", type: "quarry", timeZone: "Africa/Tunis" }], removes: [], replace: true },
      shiftPatterns: { upserts: [], removes: [], replace: true },
      assets: { upserts: assets.map((a) => ({ ...a, name: "Pelle", type: "excavator", siteId: site, status: "active", make: null, model: null, serialNumber: null, plate: null, labelCode: null })), removes: [] },
      meters: { upserts: [], removes: [] },
      materials: { upserts: [], removes: [] },
      users: { upserts: [], removes: [] },
    },
  };
}

interface Seen { url: string; claims: { iat: number; htm: string; htu: string } }

/** A tablet's database with an enrolled device, and a server that checks each request's signature before answering. */
async function setup(clockOffsetMs = 0) {
  const db = openOfflineDb(`sync-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  const device: DeviceRecord = {
    id: "this", deviceId: "0192f0a1-0000-7000-8000-0000000000d1", tenantId: "0192f0a1-0000-7000-8000-0000000000a1", siteId: site,
    privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey), pinKey: await importPinKey(new Uint8Array(32)),
    enrolledAt: "2026-10-28T10:00:00Z", clockOffsetMs,
  };
  await db.device.put(device);
  const seen: Seen[] = [];
  const answers: Response[] = [];
  const send = vi.fn(async (url: string | URL | Request, init?: RequestInit) => {
    const jws = new Headers(init?.headers).get("Authorization")?.replace("Device ", "") ?? "";
    const [header = "", payload = "", signature = ""] = jws.split(".");
    expect(await crypto.subtle.verify({ name: "ECDSA", hash: "SHA-256" }, keys.publicKey, fromBase64Url(signature) as Uint8Array<ArrayBuffer>, utf8(`${header}.${payload}`))).toBe(true);
    seen.push({ url: url instanceof Request ? url.url : url.toString(), claims: JSON.parse(new TextDecoder().decode(fromBase64Url(payload))) as Seen["claims"] });
    const next = answers.shift();
    if (!next) {
      throw new Error("no answer prepared");
    }

    return next;
  }) as unknown as typeof fetch;
  return { db, seen, send, reply: (status: number, body: unknown) => answers.push(new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })) };
}

const machineCount = (db: KarryOfflineDb) => db.assets.count();

describe("refreshing the reference data (US-P1-OFF-03, ADR-0028 §5)", () => {
  it("takes a snapshot first, then continues from the server's cursor with signed requests", async () => {
    const { db, seen, send, reply } = await setup();
    // Server times relative to now: a snapshot older than a day is replaced, so fixed dates would expire.
    const snapshotAt = new Date(Date.now() - 5 * 60_000).toISOString();
    const deltaAt = new Date().toISOString();
    const cursor = "c-snapshot";
    reply(200, changeSet(true, cursor, snapshotAt, [{ id: "a1", code: "PE-01" }, { id: "a2", code: "PE-02" }]));
    reply(200, changeSet(false, "c-delta", deltaAt, [{ id: "a3", code: "PE-03" }]));

    expect(await pullChanges(db, send)).toBe("ok");
    expect(await pullChanges(db, send)).toBe("ok");

    expect(seen.map((s) => s.url)).toEqual([changesPath, `${changesPath}?cursor=${encodeURIComponent(cursor)}`]);
    expect(seen.map((s) => [s.claims.htm, s.claims.htu])).toEqual([["GET", changesPath], ["GET", changesPath]]);
    expect(await machineCount(db)).toBe(3);
    expect(await lastRefresh(db)).toBe(deltaAt);
  });

  it("takes a new snapshot once the last one is a day old, even when deltas succeed", async () => {
    const { db, seen, send, reply } = await setup();
    const old = new Date(Date.now() - snapshotEveryMs - 60_000).toISOString();
    reply(200, changeSet(true, "c1", old, [{ id: "gone", code: "OLD" }]));
    reply(200, changeSet(true, "c2", new Date().toISOString(), [{ id: "a1", code: "PE-01" }]));

    await pullChanges(db, send);
    await pullChanges(db, send);

    expect(seen.map((s) => s.url)).toEqual([changesPath, changesPath]);
    expect((await db.assets.toArray()).map((a) => a.code)).toEqual(["PE-01"]);
  });

  it.each([[422, "sync.cursor_expired"], [400, "sync.cursor_invalid"]])("starts again from a snapshot when the cursor is refused (%i %s)", async (status, code) => {
    const { db, seen, send, reply } = await setup();
    reply(200, changeSet(true, "c1", new Date().toISOString()));
    reply(status, { code });
    reply(200, changeSet(true, "c2", new Date().toISOString(), [{ id: "a1", code: "PE-01" }]));

    await pullChanges(db, send);
    expect(await pullChanges(db, send)).toBe("ok");

    expect(seen.map((s) => s.url)).toEqual([changesPath, `${changesPath}?cursor=c1`, changesPath]);
    expect(await machineCount(db)).toBe(1);
  });

  it("corrects a wrong tablet clock with the server's time, keeps the offset and sends the request once more", async () => {
    const { db, seen, send, reply } = await setup(-3 * 60 * 60 * 1000);
    const serverTime = new Date().toISOString();
    reply(401, { code: "devices.clock_skew", serverTime });
    reply(200, changeSet(true, "c1", serverTime));

    expect(await pullChanges(db, send)).toBe("ok");

    const [late, corrected] = seen.map((s) => s.claims.iat * 1000);
    expect(Date.now() - (late ?? 0)).toBeGreaterThan(2 * 60 * 60 * 1000);
    expect(Math.abs(Date.parse(serverTime) - (corrected ?? 0))).toBeLessThan(5_000);
    expect(Math.abs((await db.device.get("this"))?.clockOffsetMs ?? Infinity)).toBeLessThan(5_000);
  });

  it("reports a revoked tablet and keeps its data untouched (TC-P1-OFF-005)", async () => {
    const { db, send, reply } = await setup();
    reply(200, changeSet(true, "c1", new Date().toISOString(), [{ id: "a1", code: "PE-01" }]));
    reply(401, { code: "devices.revoked" });

    await pullChanges(db, send);
    expect(await pullChanges(db, send)).toBe("revoked");

    expect(await machineCount(db)).toBe(1);
  });

  it("says the server is busy when it refuses with 429, and applies nothing (T-12)", async () => {
    const { db, send, reply } = await setup();
    reply(429, { code: "request.rate_limited", retryAfterSeconds: 10 });

    expect(await pullChanges(db, send)).toBe("busy");
    expect(send).toHaveBeenCalledOnce();
    expect(await lastRefresh(db)).toBeNull();
  });

  it("waits like a busy server when a service of the API is briefly down (503, hardening H-R1/H-R4)", async () => {
    const { db, send, reply } = await setup();
    reply(503, { code: "server.unavailable" });

    expect(await pullChanges(db, send)).toBe("busy");
    expect(await lastRefresh(db)).toBeNull();
  });

  it("tells no connection from a server failure, and applies nothing in either case", async () => {
    const { db, send, reply } = await setup();
    reply(500, { code: "server.unexpected_error" });

    expect(await pullChanges(db, send)).toBe("failed");
    expect(await pullChanges(db, vi.fn(() => Promise.reject(new TypeError("Failed to fetch"))))).toBe("offline");
    expect(await lastRefresh(db)).toBeNull();
  });
});

describe("statuses of the commands sent (ADR-0031 §8)", () => {
  it("takes the final status of a command answered waiting from the pull's submissions", async () => {
    const { db, send, reply } = await setup();
    const command = await enqueue(db, { type: "t.v1", payload: {} });
    await db.outbox.update(command.commandId, { status: "waiting" });
    const set = changeSet(true, "c1", new Date().toISOString());
    reply(200, { ...set, changes: { ...set.changes, submissions: { upserts: [
      { id: command.commandId, sequence: 1, status: "conflict", code: null, flags: ["x.flag"], recordId: null, at: set.serverTime },
    ], removes: [] } } });

    expect(await pullChanges(db, send)).toBe("ok");

    expect(await db.outbox.get(command.commandId)).toMatchObject({ status: "confirmed", flags: ["x.flag"] });
  });
});

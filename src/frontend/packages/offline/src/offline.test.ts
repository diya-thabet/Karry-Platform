import "fake-indexeddb/auto";
import { generateDeviceKey, importPinKey, publicJwk, sign, verify } from "@karry/crypto";
import { applyChanges, assetByLabel, lastRefresh, openOfflineDb, searchAssets, userByEmployeeId, words, type AssetRow, type ChangeSet, type KarryOfflineDb } from "./index";

const site = "0192f0c1-0000-7000-8000-000000005001";
const asset = (n: number, overrides: Partial<AssetRow> = {}): AssetRow => ({
  id: `0192f0c1-0000-7000-8000-${String(n).padStart(12, "0")}`, code: `PEL-${String(n).padStart(2, "0")}`, name: `Pelle Caterpillar 336 n°${String(n)}`,
  type: "excavator", siteId: site, status: "active", make: "Caterpillar", model: "336", serialNumber: `CAT${String(n)}`, plate: null, labelCode: `KRY1:L${String(n)}`,
  ...overrides,
});
const user = (n: number, employeeId = `OP-${String(n)}`) => ({ id: `0192f0c1-0000-7000-8000-${String(900000 + n).padStart(12, "0")}`, employeeId, name: `Opérateur ${String(n)}`, pin: null });

const allCollections = (overrides: Partial<ChangeSet["changes"]> = {}): ChangeSet["changes"] => ({
  sites: { upserts: [{ id: site, code: "CAR-NORD", name: "Carrière Nord", type: "quarry", timeZone: "Africa/Tunis" }], removes: [] },
  shiftPatterns: { upserts: [], removes: [] }, assets: { upserts: [], removes: [] }, meters: { upserts: [], removes: [] },
  materials: { upserts: [], removes: [] }, users: { upserts: [], removes: [] },
  ...overrides,
});

let db: KarryOfflineDb;
let n = 0;
beforeEach(() => { db = openOfflineDb(`test-${String(++n)}`); });
afterEach(async () => { await db.delete(); });

describe("device record (ADR-0028 §2, TC-P1-OFF-008)", () => {
  it("keeps the non-extractable keys across reopening the database, still able to sign", async () => {
    const { privateKey, publicKey } = await generateDeviceKey();
    const pinKey = await importPinKey(new Uint8Array(32).fill(3));
    await db.device.put({ id: "this", deviceId: "d1", tenantId: "t1", siteId: site, privateKey, publicKey: await publicJwk(publicKey), pinKey, enrolledAt: "2026-10-28T10:00:00Z", clockOffsetMs: 0 });
    db.close();

    const reopened = openOfflineDb(db.name);
    const device = await reopened.device.get("this");
    if (!device) throw new Error("device record lost");
    expect(device.privateKey.extractable).toBe(false);
    expect(await verify(publicKey, "x", await sign(device.privateKey, "x") as Uint8Array<ArrayBuffer>)).toBe(true);
    reopened.close();
  });
});

describe("reference data (US-P1-OFF-03 AC1/AC2)", () => {
  it("a full snapshot replaces each collection and records the refresh time", async () => {
    await db.assets.put({ ...asset(99), words: ["old"] });
    expect(await lastRefresh(db)).toBeNull();

    await applyChanges(db, { full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z", changes: allCollections({ assets: { upserts: [asset(1), asset(2)], removes: [] } }) });

    expect((await db.assets.toArray()).map((a) => a.code)).toEqual(["PEL-01", "PEL-02"]);
    expect(await lastRefresh(db)).toBe("2026-10-28T10:00:00Z");
    expect((await db.syncState.get("assets"))?.lastFullAt).toBe("2026-10-28T10:00:00Z");
  });

  it("a delta adds, replaces and removes rows (a machine moved away, an operator deactivated) and keeps the last full time", async () => {
    await applyChanges(db, { full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z", changes: allCollections({ assets: { upserts: [asset(1), asset(2)], removes: [] }, users: { upserts: [user(1), user(2)], removes: [] } }) });

    await applyChanges(db, {
      full: false, cursor: "c2", serverTime: "2026-10-28T10:05:00Z",
      changes: { assets: { upserts: [asset(1, { name: "Pelle renommée" }), asset(3)], removes: [asset(2).id] }, users: { upserts: [], removes: [user(2).id] } },
    });

    expect((await db.assets.toArray()).map((a) => `${a.code} ${a.name}`)).toEqual(["PEL-01 Pelle renommée", "PEL-03 Pelle Caterpillar 336 n°3"]);
    expect((await db.users.toArray()).map((u) => u.employeeId)).toEqual(["OP-1"]);
    expect(await db.syncState.get("assets")).toEqual({ collection: "assets", cursor: "c2", lastSuccessAt: "2026-10-28T10:05:00Z", lastFullAt: "2026-10-28T10:00:00Z" });
  });

  it("applies the same delta twice without change (the cursor overlap)", async () => {
    const delta: ChangeSet = { full: false, cursor: "c2", serverTime: "2026-10-28T10:05:00Z", changes: { assets: { upserts: [asset(1)], removes: [asset(7).id] } } };
    await applyChanges(db, delta);
    await applyChanges(db, delta);

    expect(await db.assets.count()).toBe(1);
  });

  it("keeps nothing of an answer that fails half-way, cursor included", async () => {
    await applyChanges(db, { full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z", changes: allCollections({ assets: { upserts: [asset(1)], removes: [] } }) });
    const broken = { id: undefined } as unknown as ReturnType<typeof user>;

    await expect(applyChanges(db, { full: true, cursor: "c9", serverTime: "2026-10-28T11:00:00Z", changes: allCollections({ assets: { upserts: [asset(5)], removes: [] }, users: { upserts: [broken], removes: [] } }) })).rejects.toThrow();

    expect((await db.assets.toArray()).map((a) => a.code)).toEqual(["PEL-01"]);
    expect((await db.syncState.get("assets"))?.cursor).toBe("c1");
  });
});

describe("offline search (US-P1-OFF-03 AC3, US-P1-OFF-04)", () => {
  beforeEach(async () => {
    await applyChanges(db, {
      full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z",
      changes: allCollections({
        assets: { upserts: [asset(1), asset(2, { code: "CHG-01", name: "Chargeuse Volvo", make: "Volvo", model: "L150H", plate: "152 TU 3245", labelCode: "KRY1:CHG" }), asset(3, { code: "CON-01", name: "Concasseur à mâchoires", make: "Metso" })], removes: [] },
        users: { upserts: [user(1, "OP-7")], removes: [] },
      }),
    });
  });

  it("finds machines by word prefixes, accents and case ignored", async () => {
    expect((await searchAssets(db, "pel 33")).map((a) => a.code)).toEqual(["PEL-01"]);
    expect((await searchAssets(db, "MACHOIRE")).map((a) => a.code)).toEqual(["CON-01"]);
    expect((await searchAssets(db, "152 tu")).map((a) => a.code)).toEqual(["CHG-01"]);
    expect((await searchAssets(db, "chg-01")).map((a) => a.code)).toEqual(["CHG-01"]);
    expect(await searchAssets(db, "grue")).toEqual([]);
    expect((await searchAssets(db, "  ")).map((a) => a.code)).toEqual(["CHG-01", "CON-01", "PEL-01"]);
  });

  it("selects a machine by its QR label code and a user by employee ID as typed", async () => {
    expect((await assetByLabel(db, " KRY1:CHG "))?.code).toBe("CHG-01");
    expect(await assetByLabel(db, "KRY1:NOPE")).toBeUndefined();
    expect((await userByEmployeeId(db, " op-7 "))?.name).toBe("Opérateur 1");
  });

  it("normalizes words", () => {
    expect(words("Pelle CAT-336 n°3, Évaporé")).toEqual(["pelle", "cat", "336", "n", "3", "evapore"]);
  });
});

describe("volume (TC-P1-OFF-004 early warning; the reference-device run is in the field app suite)", () => {
  it("stores 2,000 machines and 500 users and searches them quickly", async () => {
    const assets = Array.from({ length: 2000 }, (_, i) => asset(i + 1, { code: `M-${String(i + 1).padStart(4, "0")}`, name: `${["Pelle", "Chargeuse", "Tombereau", "Foreuse"][i % 4] ?? ""} ${String(i)}` }));
    const users = Array.from({ length: 500 }, (_, i) => user(i + 1));
    await applyChanges(db, { full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z", changes: allCollections({ assets: { upserts: assets, removes: [] }, users: { upserts: users, removes: [] } }) });

    const started = performance.now();
    const found = await searchAssets(db, "tomb 19");
    const operator = await userByEmployeeId(db, "OP-499");
    const elapsed = performance.now() - started;

    expect(found.length).toBeGreaterThan(0);
    expect(operator?.employeeId).toBe("OP-499");
    expect(elapsed).toBeLessThan(500);
  });
});

import "fake-indexeddb/auto";
import { openOfflineDb } from "@karry/offline";
import { assessEnd, estimatedEndValue, findActiveShift } from "./endRules";

const start = { meterKind: "hours", startedAt: "2026-11-09T06:00:00Z", startValue: "100.0" };
const at = (iso: string) => new Date(iso);

describe("what the end screen checks before signing (US-P2-FO-04 AC2)", () => {
  it("accepts up to the real length plus 30 minutes on an hour meter, and gives Δ", () => {
    expect(assessEnd({ ...start, value: "108.5", endedAt: at("2026-11-09T14:00:00Z") })).toEqual({ block: null, deltaTenths: 85 });
    expect(assessEnd({ ...start, value: "108.6", endedAt: at("2026-11-09T14:00:00Z") })).toEqual({ block: "tooLong", deltaTenths: 86 });
  });

  it("refuses a meter going backwards, an end not after the start, and a value that does not read", () => {
    expect(assessEnd({ ...start, value: "99.9", endedAt: at("2026-11-09T14:00:00Z") }).block).toBe("below");
    expect(assessEnd({ ...start, value: "100.0", endedAt: at("2026-11-09T06:00:00Z") }).block).toBe("beforeStart");
    expect(assessEnd({ ...start, value: "1,5", endedAt: at("2026-11-09T14:00:00Z") })).toEqual({ block: "value", deltaTenths: null });
  });

  it("does not limit Δ by time on a kilometre meter", () => {
    expect(assessEnd({ ...start, meterKind: "km", value: "900.0", endedAt: at("2026-11-09T14:00:00Z") }).block).toBeNull();
  });
});

describe("the operator's active shift (US-P2-FO-04 AC1)", () => {
  it("is the segment running for this operator on a machine the tablet knows, else none", async () => {
    const db = openOfflineDb(`active-${crypto.randomUUID()}`);
    await db.assets.put({
      id: "a1", code: "PE-01", name: "Pelle", type: "excavator", siteId: "s1", status: "active", make: null, model: null, serialNumber: null, plate: null, labelCode: null, words: [],
    });
    await db.meters.put({ id: "m1", assetId: "a1", kind: "hours", unit: "h", status: "ok" });
    expect(await findActiveShift(db, "u1")).toBeNull();

    await db.meterStates.put({ id: "m1", lastEnd: null, open: { entryId: "e1", version: 1, operatorId: "u1", startedAt: "2026-11-09T06:00:00Z", startValue: "100.0", shiftDate: null, shiftCode: null } });

    expect(await findActiveShift(db, "u1")).toMatchObject({ machine: { code: "PE-01" }, meterId: "m1", meterKind: "hours", unit: "h", meterFailed: false, open: { entryId: "e1", version: 1 } });
    expect(await findActiveShift(db, "u2")).toBeNull();
    await db.delete();
  });

  it("works out the end of a failed meter from the start and the operating time (US-P2-AST-02)", () => {
    expect(estimatedEndValue("1200.0", "7.5")).toBe("1207.5");
    expect(estimatedEndValue("0.0", "8")).toBe("8.0");
    expect(estimatedEndValue("1200.0", "7,5"), "as the wire writes values: a point, as the start screen asks").toBeNull();
    expect(estimatedEndValue("1200.0", "7.25")).toBeNull();
    expect(estimatedEndValue("1200.0", "")).toBeNull();
  });
});

import "fake-indexeddb/auto";
import { openOfflineDb, type MeterRow, type OutboxRecord } from "@karry/offline";
import type { MeterState } from "./meterState";
import { startCommand } from "./meterState";
import { assessStart, entryMeter, estimatedStartValue, hasRunningSegment } from "./startRules";

const me = "u-me";
const ended: MeterState = { lastEnd: { entryId: "e1", value: "100.0", at: "2026-11-09T14:00:00Z" }, open: null };
const assess = (value: string, state: MeterState = ended, meterKind = "hours", runningElsewhere = false) =>
  assessStart({ value, meterKind, state, operatorId: me, runningElsewhere });

describe("what the start screen checks before signing (US-P2-FO-01 AC4, US-P2-FO-05)", () => {
  it("TC-P2-FO-003: blocks a start below the previous end, allows the same value or more", () => {
    expect(assess("99.9").block).toBe("belowPrevious");
    expect(assess("100.0")).toEqual({ block: null, needsReason: false });
    expect(assess("100")).toEqual({ block: null, needsReason: false });
  });

  it("refuses a value that is not a meter value", () => {
    for (const value of ["", "abc", "12.34", "-1", "1,5"]) {
      expect(assess(value).block, value).toBe("value");
    }
  });

  it("asks for a reason beyond 24 hours of jump on an hour meter, not on a kilometre meter", () => {
    expect(assess("124.0")).toEqual({ block: null, needsReason: false });
    expect(assess("124.1")).toEqual({ block: null, needsReason: true });
    expect(assess("500.0", ended, "km")).toEqual({ block: null, needsReason: false });
    expect(assess("500.0", { lastEnd: null, open: null })).toEqual({ block: null, needsReason: false });
  });

  it("blocks a machine in use by someone else, and a second running shift of the same operator", () => {
    const open = { entryId: "e2", version: 1, operatorId: "u-other", startedAt: "2026-11-09T14:00:00Z", startValue: "100.0", shiftDate: null, shiftCode: null };

    expect(assess("101.0", { lastEnd: null, open }).block).toBe("machineInUse");
    expect(assess("101.0", { lastEnd: null, open: { ...open, operatorId: me } }).block).toBe("alreadyRunning");
    expect(assess("101.0", ended, "hours", true).block).toBe("alreadyRunning");
  });

  it("records on the machine's working hour meter, else its working kilometre meter", () => {
    const meter = (id: string, kind: string, status = "ok"): MeterRow => ({ id, assetId: "a1", kind, unit: kind === "hours" ? "h" : "km", status });

    expect(entryMeter([meter("k", "km"), meter("h", "hours")])?.id).toBe("h");
    expect(entryMeter([meter("k", "km"), meter("h", "hours", "replaced")])?.id).toBe("k");
    expect(entryMeter([meter("h", "hours", "faulty")])).toBeNull();
  });

  it("falls back to a failed meter, hours first, only when no meter works (US-P2-AST-02)", () => {
    const meter = (id: string, kind: string, status = "ok"): MeterRow => ({ id, assetId: "a1", kind, unit: kind === "hours" ? "h" : "km", status });

    expect(entryMeter([meter("k", "km", "failed"), meter("h", "hours", "failed")])?.id).toBe("h");
    expect(entryMeter([meter("h", "hours", "failed"), meter("k", "km")])?.id, "a working meter comes first").toBe("k");
    expect(entryMeter([meter("k", "km", "failed")])?.id).toBe("k");
  });

  it("asks no reading of a failed meter: the start is the last known end, and nothing blocks it but a running segment", () => {
    const state = { lastEnd: { entryId: "e0", value: "1200.0", at: "2026-11-09T14:00:00Z" }, open: null };

    expect(assessStart({ value: "", meterKind: "hours", state, operatorId: "u1", runningElsewhere: false, failed: true })).toEqual({ block: null, needsReason: false });
    expect(assessStart({ value: "", meterKind: "hours", state, operatorId: "u1", runningElsewhere: true, failed: true }).block).toBe("alreadyRunning");
    expect(estimatedStartValue(state)).toBe("1200.0");
    expect(estimatedStartValue({ lastEnd: null, open: null })).toBe("0.0");
  });
});

describe("whether an operator has a shift running anywhere", () => {
  it("finds it in the pulled states and in the tablet's own unsent starts", async () => {
    const db = openOfflineDb(`running-${crypto.randomUUID()}`);
    expect(await hasRunningSegment(db, me)).toBe(false);

    await db.meterStates.put({ id: "m1", lastEnd: null, open: { entryId: "e1", version: 1, operatorId: me, startedAt: "2026-11-09T06:00:00Z", startValue: "1.0", shiftDate: null, shiftCode: null } });
    expect(await hasRunningSegment(db, me)).toBe(true);
    expect(await hasRunningSegment(db, "u-other")).toBe(false);

    await db.meterStates.clear();
    const own: OutboxRecord = {
      commandId: "c1", sequence: 1, type: startCommand, deviceTime: "2026-11-09T06:00:00Z", signature: "s", status: "pending", attempts: 0, code: null, flags: [],
      answeredAt: null, blobs: [], payload: { record: { rootId: "e9", meterId: "m2", startedAt: "2026-11-09T06:00:00Z", startValue: "5.0" }, envelope: { signer: { userId: me } } },
    };
    await db.outbox.put(own);
    expect(await hasRunningSegment(db, me)).toBe(true);
    await db.delete();
  });
});

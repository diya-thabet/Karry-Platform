import type { MeterStateRow, OutboxRecord } from "@karry/offline";
import { endCommand, meterState, startCommand } from "./meterState";

const meter = "0192f0c1-0000-7000-8000-000000700001";
let sequence = 0;

function command(type: string, record: Record<string, unknown>, status: OutboxRecord["status"] = "pending", signer = "u-ours"): OutboxRecord {
  return {
    commandId: `c${String(++sequence)}`, sequence, type, deviceTime: "2026-11-09T06:00:00Z", signature: "s", status, attempts: 0, code: null, flags: [],
    answeredAt: null, blobs: [], payload: { record, envelope: { signer: { userId: signer } }, deviceSignature: "x" },
  };
}

const start = (rootId: string, startedAt: string, startValue: string, status?: OutboxRecord["status"], m = meter) =>
  command(startCommand, { rootId, version: 1, meterId: m, startedAt, startValue }, status);
const end = (rootId: string, version: number, endedAt: string, endValue: string, status?: OutboxRecord["status"]) =>
  command(endCommand, { rootId, version, endedAt, endValue }, status);

const pulled: MeterStateRow = {
  id: meter,
  lastEnd: { entryId: "e1", value: "104.0", at: "2026-11-09T10:00:00Z" },
  open: { entryId: "e2", version: 1, operatorId: "u-other", startedAt: "2026-11-09T10:00:00Z", startValue: "104.0", shiftDate: "2026-11-09", shiftCode: "M" },
};

describe("where a meter stands on the tablet (P2.5)", () => {
  it("is what the pull said when the tablet signed nothing since", () => {
    expect(meterState(pulled, meter, [])).toEqual({ lastEnd: pulled.lastEnd, open: pulled.open });
    expect(meterState(undefined, meter, [])).toEqual({ lastEnd: null, open: null });
  });

  it("takes this tablet's own end of the running segment before it is sent", () => {
    const state = meterState(pulled, meter, [end("e2", 2, "2026-11-09T14:00:00Z", "108.0")]);

    expect(state).toEqual({ lastEnd: { entryId: "e2", value: "108.0", at: "2026-11-09T14:00:00Z" }, open: null });
  });

  it("takes its own start after that end as the running segment, signed by the operator who started it", () => {
    const state = meterState(pulled, meter, [end("e2", 2, "2026-11-09T14:00:00Z", "108.0"), start("e3", "2026-11-09T14:00:00Z", "108.0")]);

    expect(state.open).toEqual({ entryId: "e3", version: 1, operatorId: "u-ours", startedAt: "2026-11-09T14:00:00Z", startValue: "108.0", shiftDate: null, shiftCode: null });
    expect(state.lastEnd?.value).toBe("108.0");
  });

  it("does not count a refused command, another meter's start, or an end of a segment it does not know", () => {
    const state = meterState(pulled, meter, [
      end("e2", 2, "2026-11-09T14:00:00Z", "99.0", "rejected"),
      start("e9", "2026-11-09T15:00:00Z", "5.0", "pending", "0192f0c1-0000-7000-8000-000000700002"),
      end("e9", 2, "2026-11-09T16:00:00Z", "6.0"),
    ]);

    expect(state).toEqual({ lastEnd: pulled.lastEnd, open: pulled.open });
  });

  it("does not show a segment as running when it started before the last end (never ended)", () => {
    const state = meterState({ id: meter, lastEnd: null, open: null }, meter, [
      start("old", "2026-11-09T06:00:00Z", "10.0", "confirmed"),
      start("new", "2026-11-09T14:00:00Z", "18.0", "confirmed"),
      end("new", 2, "2026-11-09T22:00:00Z", "26.0"),
    ]);

    expect(state).toEqual({ lastEnd: { entryId: "new", value: "26.0", at: "2026-11-09T22:00:00Z" }, open: null });
  });

  it("compares times by the instant, whatever the offset written", () => {
    const state = meterState({ id: meter, lastEnd: { entryId: "a", value: "1.0", at: "2026-11-09T10:00:00Z" }, open: null }, meter, [
      start("b", "2026-11-09T10:30:00+01:00", "1.0"),
    ]);

    expect(state.open, "09:30 UTC is before the 10:00 UTC end").toBeNull();
  });
});

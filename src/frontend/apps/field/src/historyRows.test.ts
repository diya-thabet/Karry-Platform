import type { OutboxRecord } from "@karry/offline";
import { historyRows } from "./historyRows";
import { endCommand, startCommand } from "./meterState";

let sequence = 0;
function command(type: string, record: Record<string, unknown>, overrides: Partial<OutboxRecord> = {}, signer = "u1"): OutboxRecord {
  return {
    commandId: `c${String(++sequence)}`, sequence, type, deviceTime: "2026-11-09T06:00:00Z", signature: "s", status: "confirmed", attempts: 0, code: null, flags: [],
    answeredAt: null, blobs: [], payload: { record, envelope: { signer: { userId: signer } } }, ...overrides,
  };
}

const start = (rootId: string, startedAt: string, value: string, overrides: Partial<OutboxRecord> = {}, signer?: string) =>
  command(startCommand, { rootId, assetId: "a1", startedAt, startValue: value, fuelStartPercent: 75, startPhoto: `sha256:${"a".repeat(64)}`, remarks: "froid" }, overrides, signer);
const end = (rootId: string, endedAt: string, value: string, overrides: Partial<OutboxRecord> = {}) =>
  command(endCommand, { rootId, version: 2, endedAt, endValue: value, endPhoto: `sha256:${"b".repeat(64)}` }, overrides);

const now = new Date("2026-11-10T08:00:00Z");

describe("my history (US-P2-FO-06 AC1)", () => {
  it("shows one row per entry, newest first, with its start, end, latest photo and flags", () => {
    const rows = historyRows([
      start("e1", "2026-11-09T06:00:00Z", "100.0"),
      end("e1", "2026-11-09T14:00:00Z", "108.0", { flags: ["RULE-FO-02"] }),
      start("e2", "2026-11-10T06:00:00Z", "108.0", { status: "pending" }),
    ], "u1", now);

    expect(rows.map((r) => [r.entryId, r.status, r.startValue, r.endValue, r.flags, r.photo])).toEqual([
      ["e2", "queued", "108.0", null, [], "a".repeat(64)],
      ["e1", "submitted", "100.0", "108.0", ["RULE-FO-02"], "b".repeat(64)],
    ]);
  });

  it("shows a refusal over everything, with its code and the start's values to sign it again", () => {
    const [row] = historyRows([start("e1", "2026-11-09T06:00:00Z", "100.0", { status: "rejected", code: "fieldops.entry_invalid" })], "u1", now);

    expect(row).toMatchObject({ status: "rejected", code: "fieldops.entry_invalid", start: { assetId: "a1", startValue: "100.0", fuelStartPercent: 75, remarks: "froid" } });
  });

  it("keeps only this operator's entries of the last 14 days", () => {
    const rows = historyRows([
      start("old", "2026-10-26T06:00:00Z", "1.0", { deviceTime: "2026-10-26T07:59:59Z" }),
      start("other", "2026-11-09T06:00:00Z", "1.0", {}, "u2"),
      start("mine", "2026-10-27T08:00:00Z", "1.0", { deviceTime: "2026-10-27T08:00:00Z" }),
    ], "u1", now);

    expect(rows.map((r) => r.entryId)).toEqual(["mine"]);
  });
});

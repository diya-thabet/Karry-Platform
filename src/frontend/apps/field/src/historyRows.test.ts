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

describe("a controller's decision in my history (US-P2-VER-03 AC1, P2.8)", () => {
  const outcome = (recordId: string, recordVersion: number, status: string, reasonCode: string | null = null, comment: string | null = null) =>
    ({ id: `${recordId}-${String(recordVersion)}`, recordId, recordVersion, status, reasonCode, comment });

  it("shows an approved entry, a rejected one with the reason and comment, and the newest decision of an entry", () => {
    const rows = historyRows([
      start("e1", "2026-11-09T06:00:00Z", "100.0"), end("e1", "2026-11-09T14:00:00Z", "108.0"),
      start("e2", "2026-11-09T15:00:00Z", "108.0"), end("e2", "2026-11-09T22:00:00Z", "115.0"),
      start("e3", "2026-11-10T06:00:00Z", "115.0"),
    ], "u1", now, [
      outcome("e1", 2, "approved"),
      outcome("e2", 2, "approved"), outcome("e2", 3, "rejected", "wrong_reading", "The end photo shows 112.0."),
      outcome("someone-else", 2, "rejected", "duplicate", "x"),
    ]);

    expect(rows.map((r) => [r.entryId, r.status, r.review])).toEqual([
      ["e3", "submitted", null],
      ["e2", "refused", { reasonCode: "wrong_reading", comment: "The end photo shows 112.0." }],
      ["e1", "approved", null],
    ]);
  });

  it("keeps the tablet's own states over a decision: still to send, or refused by the server", () => {
    const rows = historyRows([
      start("queued", "2026-11-09T06:00:00Z", "1.0", { status: "pending" }),
      start("refused", "2026-11-09T07:00:00Z", "1.0", { status: "rejected", code: "fieldops.entry_invalid" }),
    ], "u1", now, [outcome("queued", 2, "approved"), outcome("refused", 2, "approved")]);

    expect(rows.map((r) => [r.entryId, r.status])).toEqual([["refused", "rejected"], ["queued", "queued"]]);
  });
});

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

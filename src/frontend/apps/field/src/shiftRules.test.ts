// @vitest-environment node
import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { endFlags, endRecordProblem, instantTicks, meterTenths, meterText, overlaps, startFlags, startRecordProblem, type MeterKind } from "./shiftRules";

interface Named {
  name: string;
}

interface Vectors {
  meterValues: { valid: { text: string; tenths: number }[]; invalid: string[] };
  instants: { valid: string[]; invalid: string[] };
  endFlags: (Named & { meterKind: MeterKind; startedAt: string; startValue: string; endedAt: string; endValue: string; flags: string[] })[];
  startFlags: (Named & { continuesFrom: string | null; startValue: string; flags: string[] })[];
  overlaps: (Named & { a: { start: string; end: string | null }; b: { start: string; end: string | null }; overlaps: boolean })[];
  records: Record<"start" | "end", { valid: (Named & { record: unknown })[]; invalid: (Named & { record: unknown })[] }>;
}

// TC-P2-FO-012: the file the server's ShiftEntryVectorsTests read too.
const vectors = JSON.parse(readFileSync(new URL("../../../../../tests/vectors/shift-entry-rules.json", import.meta.url), "utf8")) as Vectors;

const ticks = (text: string) => {
  const value = instantTicks(text);
  if (value === null) {
    throw new Error(`not an instant: ${text}`);
  }

  return value;
};

const tenths = (text: string) => {
  const value = meterTenths(text);
  if (value === null) {
    throw new Error(`not a meter value: ${text}`);
  }

  return value;
};

describe("shift entry rules (ADR-0034, shared vectors TC-P2-FO-012)", () => {
  it.each(vectors.meterValues.valid.map((v) => [v.text, v.tenths] as const))("reads the meter value '%s' as %i tenths", (text, expected) => {
    expect(meterTenths(text)).toBe(expected);
  });

  it.each(vectors.meterValues.invalid.map((v) => [v] as const))("refuses the meter value '%s'", (text) => {
    expect(meterTenths(text)).toBeNull();
  });

  it("writes tenths back as the wire wants them", () => {
    expect([0, 10, 124825, 99999999].map(meterText)).toEqual(["0.0", "1.0", "12482.5", "9999999.9"]);
    expect(meterTenths(12482.5)).toBeNull();
  });

  it.each(vectors.instants.valid.map((v) => [v] as const))("reads the time %s", (text) => {
    expect(instantTicks(text)).not.toBeNull();
  });

  it.each(vectors.instants.invalid.map((v) => [v] as const))("refuses the time %s", (text) => {
    expect(instantTicks(text)).toBeNull();
  });

  it("counts offsets and ticks", () => {
    expect(ticks("2026-11-09T07:00:00+01:00")).toBe(ticks("2026-11-09T06:00:00Z"));
    expect(ticks("2026-11-09T06:00:00.0000001Z") - ticks("2026-11-09T06:00:00Z")).toBe(1n);
    expect(ticks("1970-01-01T00:00:01Z")).toBe(10_000_000n);
  });

  it.each(vectors.endFlags.map((v) => [v.name, v] as const))("raises the end flags of the vectors: %s", (_name, v) => {
    expect(endFlags(v.meterKind, ticks(v.startedAt), tenths(v.startValue), ticks(v.endedAt), tenths(v.endValue))).toEqual(v.flags);
  });

  it.each(vectors.startFlags.map((v) => [v.name, v] as const))("raises the start flags of the vectors: %s", (_name, v) => {
    expect(startFlags(v.continuesFrom === null ? null : tenths(v.continuesFrom), tenths(v.startValue))).toEqual(v.flags);
  });

  it.each(vectors.overlaps.map((v) => [v.name, v] as const))("finds overlaps as the vectors say, in either order: %s", (_name, v) => {
    const end = (text: string | null) => (text === null ? null : ticks(text));
    expect(overlaps(ticks(v.a.start), end(v.a.end), ticks(v.b.start), end(v.b.end))).toBe(v.overlaps);
    expect(overlaps(ticks(v.b.start), end(v.b.end), ticks(v.a.start), end(v.a.end))).toBe(v.overlaps);
  });

  it.each(vectors.records.start.valid.map((v) => [v.name, v.record] as const))("accepts the start record: %s", (_name, record) => {
    expect(startRecordProblem(record)).toBeNull();
  });

  it.each(vectors.records.start.invalid.map((v) => [v.name, v.record] as const))("refuses the start record: %s", (_name, record) => {
    expect(startRecordProblem(record)).not.toBeNull();
  });

  it.each(vectors.records.end.valid.map((v) => [v.name, v.record] as const))("accepts the end record: %s", (_name, record) => {
    expect(endRecordProblem(record)).toBeNull();
  });

  it.each(vectors.records.end.invalid.map((v) => [v.name, v.record] as const))("refuses the end record: %s", (_name, record) => {
    expect(endRecordProblem(record)).not.toBeNull();
  });
});

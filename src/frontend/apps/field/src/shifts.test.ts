// @vitest-environment node
import { readFileSync } from "node:fs";
import type { ShiftPatternRow } from "@karry/offline";
import { instantOf, localDate, shiftAt } from "./shifts";

interface Vectors {
  patterns: Record<string, Omit<ShiftPatternRow, "id" | "siteId">[]>;
  cases: {
    name: string;
    timeZone: string;
    pattern: string;
    nonWorkingDays: string[];
    at: string;
    shift: { date: string; code: string; startsAt: string; endsAt: string } | null;
  }[];
}

// TC-P2-FO-001 (logic part): the file the server's ShiftCalendarVectorsTests read too.
const vectors = JSON.parse(readFileSync(new URL("../../../../../tests/vectors/shift-resolution.json", import.meta.url), "utf8")) as Vectors;

const versions = (pattern: string): ShiftPatternRow[] =>
  (vectors.patterns[pattern] ?? []).map((v, i) => ({ id: `${pattern}-${String(i)}`, siteId: "s1", effectiveFrom: v.effectiveFrom, shifts: v.shifts }));

describe("shift resolution on the tablet (ADR-0024, ADR-0034 §2, shared vectors)", () => {
  it.each(vectors.cases.map((c) => [c.name, c] as const))("%s", (_name, c) => {
    const shift = shiftAt(c.timeZone, versions(c.pattern), new Set(c.nonWorkingDays), Date.parse(c.at));

    expect(shift === null ? null : { date: shift.date, code: shift.code, startsAt: shift.startsAt, endsAt: shift.endsAt })
      .toEqual(c.shift === null ? null : { date: c.shift.date, code: c.shift.code, startsAt: Date.parse(c.shift.startsAt), endsAt: Date.parse(c.shift.endsAt) });
  });

  it("reads local times through gaps and overlaps as the server does", () => {
    const local = (text: string) => Date.parse(`${text}Z`);

    expect(instantOf("Europe/Paris", local("2026-03-29T02:30:00"))).toBe(Date.parse("2026-03-29T01:30:00Z"));
    expect(instantOf("Europe/Paris", local("2026-03-29T03:00:00"))).toBe(Date.parse("2026-03-29T01:00:00Z"));
    expect(instantOf("Europe/Paris", local("2026-10-25T02:30:00"))).toBe(Date.parse("2026-10-25T00:30:00Z"));
    expect(instantOf("Africa/Tunis", local("2026-11-09T06:00:00"))).toBe(Date.parse("2026-11-09T05:00:00Z"));
    expect(localDate("America/Lima", Date.parse("2026-11-10T04:00:00Z"))).toBe("2026-11-09");
  });
});

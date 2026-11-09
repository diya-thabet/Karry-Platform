/**
 * The rules of a shift entry (ADR-0034 §3, §5, §6), as the server applies them: the tablet checks a record before the
 * operator signs it, so a signed fact is never refused for its shape, and shows the flags the server will raise.
 * Both sides read `tests/vectors/shift-entry-rules.json` in their tests (TC-P2-FO-012). Meter values are whole tenths
 * and times are 100 ns ticks, so nothing goes through floating point.
 */

export type MeterKind = "hours" | "km";

export const recordType = "fieldops.shift_entry";

export const flags = {
  continuity: "RULE-FO-01",
  delta: "RULE-FO-02",
  endBeforeStart: "END_BEFORE_START",
  overlap: "OVERLAP",
} as const;

/** The stoppage reasons of US-P2-FO-04 AC1 (a fixed list until the owner confirms the codes, ADR-0034 §8). */
export const stoppageReasons = ["breakdown", "weather", "no_material", "no_operator", "other"] as const;

export type StoppageReason = (typeof stoppageReasons)[number];

export interface Stoppage {
  reason: StoppageReason;
  minutes: number;
}

/** A start as signed: `fieldops.shift_entry.start.v1`. The operator is the envelope's signer, never a member here. */
export interface StartRecord {
  rootId: string;
  version: 1;
  assetId: string;
  meterId: string;
  startedAt: string;
  startValue: string;
  fuelStartPercent: number;
  startPhoto: string;
  previousRootId?: string;
  remarks?: string;
}

/** An end as signed: `fieldops.shift_entry.end.v1`, the next version of the entry. */
export interface EndRecord {
  rootId: string;
  version: number;
  endedAt: string;
  endValue: string;
  fuelEndPercent: number;
  endPhoto: string;
  stoppages?: Stoppage[];
  remarks?: string;
}

export const maxRemarks = 1000;
export const maxStoppages = 20;
const ticksPerTenthHour = 3_600_000_000n;
const deltaToleranceTicks = 18_000_000_000n; // 30 min

const meterValuePattern = /^(0|[1-9][0-9]{0,6})(\.[0-9])?$/;
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const hashPattern = /^sha256:[0-9a-f]{64}$/;
const instantPattern = /^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{2}):([0-9]{2}):([0-9]{2})(?:\.([0-9]{1,7}))?(?:Z|([+-])([0-9]{2}):([0-9]{2}))$/;

/** A meter value as written on the wire (`"12482.5"`: at most one decimal, no sign, no leading zero) in tenths; null otherwise. */
export function meterTenths(text: unknown): number | null {
  if (typeof text !== "string") {
    return null;
  }

  const match = meterValuePattern.exec(text);
  if (match === null) {
    return null;
  }

  return Number(match[1]) * 10 + (match[2] === undefined ? 0 : Number(match[2].slice(1)));
}

/** A meter value in tenths written as the wire wants it (`124825` → `"12482.5"`). */
export function meterText(tenths: number): string {
  return `${String(Math.trunc(tenths / 10))}.${String(tenths % 10)}`;
}

const daysIn = (year: number, month: number) => (month === 2 ? (year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0) ? 29 : 28) : [4, 6, 9, 11].includes(month) ? 30 : 31);

/** An ISO 8601 time with its offset (`2026-11-09T06:00:00Z`, `…+01:00`, up to 7 decimals) in 100 ns ticks since 1970; null otherwise. */
export function instantTicks(text: unknown): bigint | null {
  if (typeof text !== "string") {
    return null;
  }

  const m = instantPattern.exec(text);
  if (m === null) {
    return null;
  }

  const [year, month, day, hour, minute, second] = [m[1], m[2], m[3], m[4], m[5], m[6]].map(Number) as [number, number, number, number, number, number];
  const offsetHours = m[9] === undefined ? 0 : Number(m[9]);
  const offsetMinutes = m[10] === undefined ? 0 : Number(m[10]);
  if (month < 1 || month > 12 || day < 1 || day > daysIn(year, month) || hour > 23 || minute > 59 || second > 59
    || offsetMinutes > 59 || offsetHours * 60 + offsetMinutes > 14 * 60) {
    return null;
  }

  const date = new Date(0);
  date.setUTCFullYear(year, month - 1, day);
  date.setUTCHours(hour, minute, second, 0);
  const fraction = BigInt((m[7] ?? "").padEnd(7, "0"));
  const offset = BigInt((offsetHours * 60 + offsetMinutes) * 60) * 10_000_000n * (m[8] === "-" ? -1n : 1n);
  return BigInt(date.getTime()) * 10_000n + fraction - offset;
}

/** The flags of an end (ADR-0034 §6): end not after start; Δ negative or, on an hour meter, beyond the real length + 0.5 h. */
export function endFlags(meterKind: MeterKind, startedAt: bigint, startTenths: number, endedAt: bigint, endTenths: number): string[] {
  const raised: string[] = [];
  if (endedAt <= startedAt) {
    raised.push(flags.endBeforeStart);
  }

  const delta = endTenths - startTenths;
  const tooLarge = meterKind === "hours" && endedAt > startedAt && BigInt(delta) * ticksPerTenthHour > endedAt - startedAt + deltaToleranceTicks;
  if (delta < 0 || tooLarge) {
    raised.push(flags.delta);
  }

  return raised;
}

/** The flag of a start (RULE-FO-01): its value differs from the end it continues; nothing to compare with, nothing to flag. */
export function startFlags(continuesFromTenths: number | null, startTenths: number): string[] {
  return continuesFromTenths !== null && continuesFromTenths !== startTenths ? [flags.continuity] : [];
}

/** Whether two segments `[start, end)` share time; a segment without an end still runs, one ending at or before its start holds none. */
export function overlaps(aStart: bigint, aEnd: bigint | null, bStart: bigint, bEnd: bigint | null): boolean {
  if ((aEnd !== null && aEnd <= aStart) || (bEnd !== null && bEnd <= bStart)) {
    return false;
  }

  return (bEnd === null || aStart < bEnd) && (aEnd === null || bStart < aEnd);
}

type Fields = Record<string, unknown>;

function hasOnly(record: unknown, required: readonly string[], optional: readonly string[]): record is Fields {
  if (typeof record !== "object" || record === null || Array.isArray(record)) {
    return false;
  }

  const members = Object.entries(record);
  return required.every((name) => Object.hasOwn(record, name))
    && members.every(([name, value]) => (required.includes(name) || optional.includes(name)) && value !== null && value !== undefined);
}

const isUuid = (value: unknown) => typeof value === "string" && uuidPattern.test(value);
const isHash = (value: unknown) => typeof value === "string" && hashPattern.test(value);
const isWhole = (value: unknown, min: number, max: number) => typeof value === "number" && Number.isInteger(value) && value >= min && value <= max;
const isOptionalText = (record: Fields, name: string) =>
  !Object.hasOwn(record, name) || (typeof record[name] === "string" && record[name].length <= maxRemarks && record[name].trim().length > 0);

/** Why a start record would be refused (`fieldops.entry_invalid`): the first member at fault, or null when the server reads it. */
export function startRecordProblem(record: unknown): string | null {
  if (!hasOnly(record, ["rootId", "version", "assetId", "meterId", "startedAt", "startValue", "fuelStartPercent", "startPhoto"], ["previousRootId", "remarks"])) {
    return "shape";
  }

  const checks: [string, boolean][] = [
    ["rootId", isUuid(record.rootId)],
    ["version", isWhole(record.version, 1, 1)],
    ["assetId", isUuid(record.assetId)],
    ["meterId", isUuid(record.meterId)],
    ["startedAt", instantTicks(record.startedAt) !== null],
    ["startValue", meterTenths(record.startValue) !== null],
    ["fuelStartPercent", isWhole(record.fuelStartPercent, 0, 100)],
    ["startPhoto", isHash(record.startPhoto)],
    ["remarks", isOptionalText(record, "remarks")],
    ["previousRootId", !Object.hasOwn(record, "previousRootId") || (isUuid(record.previousRootId) && record.previousRootId !== record.rootId)],
  ];
  return checks.find(([, ok]) => !ok)?.[0] ?? null;
}

/** Why an end record would be refused (`fieldops.entry_invalid`): the first member at fault, or null when the server reads it. */
export function endRecordProblem(record: unknown): string | null {
  if (!hasOnly(record, ["rootId", "version", "endedAt", "endValue", "fuelEndPercent", "endPhoto"], ["stoppages", "remarks"])) {
    return "shape";
  }

  const checks: [string, boolean][] = [
    ["rootId", isUuid(record.rootId)],
    ["version", isWhole(record.version, 2, 2_147_483_647)],
    ["endedAt", instantTicks(record.endedAt) !== null],
    ["endValue", meterTenths(record.endValue) !== null],
    ["fuelEndPercent", isWhole(record.fuelEndPercent, 0, 100)],
    ["endPhoto", isHash(record.endPhoto)],
    ["remarks", isOptionalText(record, "remarks")],
    ["stoppages", !Object.hasOwn(record, "stoppages") || stoppagesValid(record.stoppages)],
  ];
  return checks.find(([, ok]) => !ok)?.[0] ?? null;
}

function stoppagesValid(list: unknown): boolean {
  return Array.isArray(list) && list.length <= maxStoppages
    && list.every((item) => hasOnly(item, ["reason", "minutes"], [])
      && typeof item.reason === "string" && (stoppageReasons as readonly string[]).includes(item.reason) && isWhole(item.minutes, 1, 24 * 60));
}

import type { ShiftPatternRow } from "@karry/offline";

/**
 * Which shift an instant belongs to, on the tablet, with the server's rules (ADR-0024, ADR-0034 §2,
 * `ShiftCalendar.Resolve`): wall-clock shifts on the site's clock, the newest pattern version effective on the date,
 * nothing starting on a non-working day; the shift running at the instant, else the one starting within 60 minutes
 * after it. Local times resolve as NodaTime does on the server: inside a spring-forward gap a time moves forward by
 * the gap, inside a fall-back overlap it takes the earlier offset. Both sides read `tests/vectors/shift-resolution.json`.
 */

export interface ResolvedShift {
  /** The site's local date the shift starts on, "YYYY-MM-DD". */
  date: string;
  code: string;
  name: string;
  /** Epoch milliseconds. */
  startsAt: number;
  endsAt: number;
}

/** How long before a shift's start an entry still belongs to it (machines are warmed up). */
export const earlyStartMs = 60 * 60 * 1000;

const dayCodes = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"] as const;
const halfDayMs = 12 * 60 * 60 * 1000;
const dayMs = 24 * 60 * 60 * 1000;
const formats = new Map<string, Intl.DateTimeFormat>();

function format(timeZone: string): Intl.DateTimeFormat {
  let found = formats.get(timeZone);
  if (!found) {
    found = new Intl.DateTimeFormat("en-US", {
      timeZone, hourCycle: "h23", year: "numeric", month: "2-digit", day: "2-digit", hour: "2-digit", minute: "2-digit", second: "2-digit",
    });
    formats.set(timeZone, found);
  }

  return found;
}

/** The wall-clock fields at an instant on the zone's clock, as if they were UTC (epoch milliseconds, whole seconds). */
function wallClock(timeZone: string, instant: number): number {
  const parts: Record<string, number> = {};
  for (const part of format(timeZone).formatToParts(new Date(instant))) {
    if (part.type !== "literal") {
      parts[part.type] = Number(part.value);
    }
  }

  return Date.UTC(parts.year ?? 0, (parts.month ?? 1) - 1, parts.day ?? 1, parts.hour ?? 0, parts.minute ?? 0, parts.second ?? 0);
}

/** The zone's offset from UTC at an instant, in milliseconds. */
function offsetAt(timeZone: string, instant: number): number {
  return wallClock(timeZone, instant) - Math.floor(instant / 1000) * 1000;
}

/**
 * The instant of a local date and time on the zone's clock (`local`: those fields as if they were UTC). A time that
 * happens twice (fall-back overlap) takes the earlier instant; one that does not happen (spring-forward gap) moves
 * forward by the gap, that is, it is read with the offset in force before the change.
 */
export function instantOf(timeZone: string, local: number): number {
  const before = offsetAt(timeZone, local - halfDayMs);
  const after = offsetAt(timeZone, local + halfDayMs);
  const valid = [...new Set([before, after])].map((offset) => local - offset).filter((instant) => offsetAt(timeZone, instant) === local - instant);
  return valid.length > 0 ? Math.min(...valid) : local - before;
}

const isoDate = (local: number) => new Date(local).toISOString().slice(0, 10);
const dateAt = (date: string) => Date.parse(`${date}T00:00:00Z`);
const minutes = (time: string) => Number(time.slice(0, 2)) * 60 + Number(time.slice(3, 5));

/** The site's local date at an instant, "YYYY-MM-DD". */
export function localDate(timeZone: string, instant: number): string {
  return isoDate(wallClock(timeZone, instant));
}

/** The shifts starting on each local date from `from` to `to` (inclusive), ordered by start then code. */
export function shiftsBetween(timeZone: string, versions: readonly ShiftPatternRow[], nonWorkingDays: ReadonlySet<string>, from: string, to: string): ResolvedShift[] {
  const ordered = [...versions].sort((a, b) => (a.effectiveFrom < b.effectiveFrom ? 1 : a.effectiveFrom > b.effectiveFrom ? -1 : 0));
  const result: ResolvedShift[] = [];
  for (let day = dateAt(from); day <= dateAt(to); day += dayMs) {
    const date = isoDate(day);
    const version = ordered.find((v) => v.effectiveFrom <= date);
    if (nonWorkingDays.has(date) || !version) {
      continue;
    }

    const weekday = dayCodes[new Date(day).getUTCDay()] ?? "sun";
    for (const shift of version.shifts.filter((s) => s.days.includes(weekday))) {
      const startMinutes = minutes(shift.start);
      const endMinutes = minutes(shift.end);
      const startsAt = instantOf(timeZone, day + startMinutes * 60_000);
      const endsAt = instantOf(timeZone, day + (endMinutes > startMinutes ? 0 : dayMs) + endMinutes * 60_000);
      if (endsAt > startsAt) {
        result.push({ date, code: shift.code, name: shift.name, startsAt, endsAt });
      }
    }
  }

  return result.sort((a, b) => a.startsAt - b.startsAt || (a.code < b.code ? -1 : a.code > b.code ? 1 : 0));
}

/** The shift an entry made at `instant` belongs to, or null (a gap between shifts, a non-working day). */
export function shiftAt(timeZone: string, versions: readonly ShiftPatternRow[], nonWorkingDays: ReadonlySet<string>, instant: number): ResolvedShift | null {
  const today = dateAt(localDate(timeZone, instant));
  const near = shiftsBetween(timeZone, versions, nonWorkingDays, isoDate(today - dayMs), isoDate(today + dayMs));
  return near.find((s) => s.startsAt <= instant && instant < s.endsAt)
    ?? near.find((s) => s.startsAt - earlyStartMs <= instant && instant < s.startsAt)
    ?? null;
}

import type { components } from "@karry/api-client";
import type { ReviewItem, ShiftEntry } from "./queue";

type Shift = components["schemas"]["ShiftInstanceResponse"];
type Machine = components["schemas"]["AssetResponse"];

/** Where a machine stands in a shift (US-P2-VER-08 AC1). */
export type MachineState = "not_started" | "running" | "missing_end" | "ended";

/** How the decisions on a machine's entries of the shift stand. */
export type ReviewState = "none" | "pending" | "decided";

export interface BoardMachine {
  machine: Machine;
  state: MachineState;
  review: ReviewState;
  flags: number;
  entries: ShiftEntry[];
}

export interface BoardShift {
  shift: Shift;
  machines: BoardMachine[];
  /** Planned: the site's machines in service; the others count what they say. */
  counts: { planned: number; started: number; ended: number; missingEnd: number; pendingReviews: number; flagged: number };
}

/** A segment still open this long after its shift ended has a missing end (RULE-FO-08's default grace, ADR-0038 §5). */
export const missingEndAfterMs = 60 * 60_000;

/**
 * The board of one site and date (US-P2-VER-08): for each shift, each machine of the site in service, from the entries
 * resolved to that shift (their shift code) and the review items of those entries. Several segments of one machine in
 * a shift (a handover) count together: open if one is open, ended when all are.
 */
export function boardOf(shifts: Shift[], machines: Machine[], entries: ShiftEntry[], items: ReviewItem[], now: Date): BoardShift[] {
  const inService = machines.filter((machine) => machine.status === "active");
  return shifts.map((shift) => {
    const rows = inService.map((machine): BoardMachine => {
      const mine = entries.filter((entry) => entry.assetId === machine.id && entry.shift?.code === shift.code && entry.shift.date === shift.date);
      const open = mine.some((entry) => entry.status === "open");
      const late = now.getTime() >= new Date(shift.endsAt).getTime() + missingEndAfterMs;
      const state: MachineState = mine.length === 0 ? "not_started" : open ? (late ? "missing_end" : "running") : "ended";
      const reviews = items.filter((item) => mine.some((entry) => entry.id === item.recordId));
      const review: ReviewState = reviews.length === 0
        ? "none"
        : reviews.some((item) => item.status === "pending" || item.status === "awaiting_second") ? "pending" : "decided";
      return { machine, state, review, flags: mine.reduce((sum, entry) => sum + entry.flags.length, 0), entries: mine };
    });
    return {
      shift,
      machines: rows,
      counts: {
        planned: rows.length,
        started: rows.filter((row) => row.state !== "not_started").length,
        ended: rows.filter((row) => row.state === "ended").length,
        missingEnd: rows.filter((row) => row.state === "missing_end").length,
        pendingReviews: rows.filter((row) => row.review === "pending").length,
        flagged: rows.filter((row) => row.flags > 0).length,
      },
    };
  });
}

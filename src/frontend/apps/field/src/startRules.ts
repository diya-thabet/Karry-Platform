import type { KarryOfflineDb, MeterRow } from "@karry/offline";
import { readMeterState, startCommand, type MeterState } from "./meterState";
import { meterTenths } from "./shiftRules";

/** A meter jump beyond this many hours since the last end needs a reason (US-P2-FO-01 AC4; a company setting later). */
export const unusualJumpHours = 24;

/**
 * Why a start cannot be signed, as the start screen explains it (US-P2-FO-01 AC4, US-P2-FO-05):
 * - `value`: not a meter value (at most one decimal);
 * - `belowPrevious`: below the meter's last end (TC-P2-FO-003; a meter event will allow it, P2.7);
 * - `machineInUse`: another operator's segment still runs on this machine (end it first, then continue after it);
 * - `alreadyRunning`: this operator has a segment running, here or on another machine (US-P2-FO-05 AC2).
 */
export type StartBlock = "value" | "belowPrevious" | "machineInUse" | "alreadyRunning";

export interface StartAssessment {
  block: StartBlock | null;
  /** The meter jumped more than {@link unusualJumpHours} since the last end: the operator must say why (in the remarks). */
  needsReason: boolean;
}

/** What the start screen checks before the operator may sign; the server flags the same facts anyway. */
export function assessStart(input: {
  value: string;
  meterKind: string;
  state: MeterState;
  operatorId: string;
  /** This operator's running segment on any machine, if any. */
  runningElsewhere: boolean;
  /** The meter is failed (US-P2-AST-02): no reading is asked, the start is the last known end (an estimate). */
  failed?: boolean;
}): StartAssessment {
  const { state } = input;
  if (state.open !== null) {
    return { block: state.open.operatorId === input.operatorId ? "alreadyRunning" : "machineInUse", needsReason: false };
  }

  if (input.runningElsewhere) {
    return { block: "alreadyRunning", needsReason: false };
  }

  if (input.failed === true) {
    return { block: null, needsReason: false };
  }

  const tenths = meterTenths(input.value);
  if (tenths === null) {
    return { block: "value", needsReason: false };
  }

  const previous = state.lastEnd === null ? null : meterTenths(state.lastEnd.value);
  if (previous !== null && tenths < previous) {
    return { block: "belowPrevious", needsReason: false };
  }

  return { block: null, needsReason: input.meterKind === "hours" && previous !== null && tenths - previous > unusualJumpHours * 10 };
}

/**
 * The meter a shift entry is recorded on: the machine's working hour meter, else its working kilometre meter, else a
 * failed one (hours first), on which the entry is estimated from the operating time (US-P2-AST-02, failed-meter mode).
 */
export function entryMeter(meters: readonly MeterRow[]): MeterRow | null {
  const pick = (status: string) => {
    const usable = meters.filter((m) => m.status === status);
    return usable.find((m) => m.kind === "hours") ?? usable.find((m) => m.kind === "km") ?? null;
  };
  return pick("ok") ?? pick("failed");
}

/** The start of an entry on a failed meter: its last known end, or zero when the tablet knows none (an estimate either way). */
export function estimatedStartValue(state: MeterState): string {
  return state.lastEnd?.value ?? "0.0";
}

/** Whether this operator has a segment running anywhere, as the tablet knows it (pulled states and its own commands). */
export async function hasRunningSegment(db: KarryOfflineDb, operatorId: string): Promise<boolean> {
  const pulled = await db.meterStates.filter((s) => s.open?.operatorId === operatorId).toArray();
  const ours = await db.outbox.where("status").anyOf("pending", "waiting", "confirmed").filter((c) => c.type === startCommand).toArray();
  const meters = new Set([...pulled.map((s) => s.id), ...ours.map((c) => (c.payload as { record?: { meterId?: unknown } }).record?.meterId).filter((m): m is string => typeof m === "string")]);
  for (const meter of meters) {
    if ((await readMeterState(db, meter)).open?.operatorId === operatorId) {
      return true;
    }
  }

  return false;
}


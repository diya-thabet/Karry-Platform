import type { KarryOfflineDb, StoredAsset } from "@karry/offline";
import { readMeterState, type MeterState } from "./meterState";
import { endFlags, flags, instantTicks, meterTenths, meterText, type MeterKind } from "./shiftRules";

/** The running segment of an operator, with its machine and meter, as the home screen shows it ("My active shift"). */
export interface ActiveShift {
  machine: StoredAsset;
  meterId: string;
  meterKind: string;
  unit: string;
  /** The meter is failed now: the operator gives the operating time (or distance) and the end is estimated from it. */
  meterFailed: boolean;
  open: NonNullable<MeterState["open"]>;
}

/**
 * The operator's running segment on this tablet's machines, from the pulled states and the tablet's own commands
 * (US-P2-FO-04 AC1: an end starts from "My active shift"), or null.
 */
export async function findActiveShift(db: KarryOfflineDb, operatorId: string): Promise<ActiveShift | null> {
  const candidates = new Set((await db.meterStates.toArray()).filter((s) => s.open?.operatorId === operatorId).map((s) => s.id));
  for (const command of await db.outbox.where("status").anyOf("pending", "waiting", "confirmed").toArray()) {
    const meterId = (command.payload as { record?: { meterId?: unknown } }).record?.meterId;
    if (typeof meterId === "string") {
      candidates.add(meterId);
    }
  }

  for (const meterId of candidates) {
    const { open } = await readMeterState(db, meterId);
    const meter = await db.meters.get(meterId);
    const machine = meter ? await db.assets.get(meter.assetId) : undefined;
    if (open?.operatorId === operatorId && meter && machine) {
      return { machine, meterId, meterKind: meter.kind, unit: meter.unit, meterFailed: meter.status === "failed", open };
    }
  }

  return null;
}

/**
 * Why an end cannot be signed, as the end screen explains it (US-P2-FO-04 AC2): `value` not a meter value; `below`
 * the meter went backwards (Δ negative); `tooLong` an hour meter ran more than the real time plus 30 minutes;
 * `beforeStart` the end is not after the start. A meter event will allow the first two (P2.7).
 */
export type EndBlock = "value" | "below" | "tooLong" | "beforeStart";

export interface EndAssessment {
  block: EndBlock | null;
  /** Δ in tenths when the value reads, else null. */
  deltaTenths: number | null;
}

/**
 * The end of an entry on a failed meter (US-P2-AST-02): its start plus the operating time (hours) or distance (km) the
 * operator gives, as a meter value; null when the amount is not a number with at most one decimal. The signed record
 * keeps its shape (a start and an end value); the server marks the entry estimated because the meter was failed.
 */
export function estimatedEndValue(startValue: string, amount: string): string | null {
  const start = meterTenths(startValue);
  const run = meterTenths(amount);
  return start === null || run === null ? null : meterText(start + run);
}

/** The checks of the end screen, with the rules the server applies (shared vectors, `shiftRules.ts`). */
export function assessEnd(input: { value: string; meterKind: string; startedAt: string; startValue: string; endedAt: Date }): EndAssessment {
  const end = meterTenths(input.value);
  const start = meterTenths(input.startValue);
  const startedAt = instantTicks(input.startedAt);
  if (end === null || start === null || startedAt === null) {
    return { block: "value", deltaTenths: null };
  }

  const endedAt = BigInt(input.endedAt.getTime()) * 10_000n;
  const raised = endFlags(input.meterKind as MeterKind, startedAt, start, endedAt, end);
  const deltaTenths = end - start;
  if (raised.includes(flags.endBeforeStart)) {
    return { block: "beforeStart", deltaTenths };
  }

  if (raised.includes(flags.delta)) {
    return { block: deltaTenths < 0 ? "below" : "tooLong", deltaTenths };
  }

  return { block: null, deltaTenths };
}

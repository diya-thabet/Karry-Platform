import type { KarryOfflineDb, MeterStateRow, OutboxRecord } from "@karry/offline";
import { instantTicks } from "./shiftRules";

export const startCommand = "fieldops.shift_entry.start.v1";
export const endCommand = "fieldops.shift_entry.end.v1";

/** A meter's last end and its running segment as this tablet knows them now: the pull, then its own commands. */
export type MeterState = Omit<MeterStateRow, "id">;

interface Segment {
  entryId: string;
  version: number;
  operatorId: string | null;
  startedAt: string | null;
  startValue: string | null;
  shiftDate: string | null;
  shiftCode: string | null;
  endedAt: string | null;
  endValue: string | null;
}

interface Signed {
  record?: Record<string, unknown>;
  envelope?: { signer?: { userId?: unknown } };
}

const text = (value: unknown): string | null => (typeof value === "string" ? value : null);
const ticks = (value: string | null) => (value === null ? null : instantTicks(value));

/**
 * Where a meter stands for the start and end screens (P2.5): the pulled state (FieldOps `meterStates`), corrected by
 * the shift commands this tablet signed and may not have sent yet, which are newer by construction. The last end is
 * the latest end known; the running segment is the latest started one without an end, unless it started before that
 * end (a segment never ended, as the server reads it: ADR-0034 §5). A refused command does not count.
 */
export function meterState(pulled: MeterStateRow | undefined, meterId: string, commands: readonly OutboxRecord[]): MeterState {
  const segments = new Map<string, Segment>();
  const blank = (entryId: string): Segment => ({
    entryId, version: 1, operatorId: null, startedAt: null, startValue: null, shiftDate: null, shiftCode: null, endedAt: null, endValue: null,
  });
  if (pulled?.lastEnd) {
    segments.set(pulled.lastEnd.entryId, { ...blank(pulled.lastEnd.entryId), version: 2, endedAt: pulled.lastEnd.at, endValue: pulled.lastEnd.value });
  }

  if (pulled?.open) {
    segments.set(pulled.open.entryId, { ...blank(pulled.open.entryId), ...pulled.open });
  }

  const ours = [...commands].filter((c) => c.status !== "rejected").sort((a, b) => a.sequence - b.sequence);
  for (const command of ours) {
    const { record, envelope } = command.payload as Signed;
    const entryId = text(record?.rootId);
    if (!record || entryId === null) {
      continue;
    }

    if (command.type === startCommand && record.meterId === meterId) {
      segments.set(entryId, {
        ...(segments.get(entryId) ?? blank(entryId)),
        operatorId: text(envelope?.signer?.userId), startedAt: text(record.startedAt), startValue: text(record.startValue),
      });
    } else if (command.type === endCommand && segments.has(entryId)) {
      const segment = segments.get(entryId) ?? blank(entryId);
      segments.set(entryId, { ...segment, version: Number(record.version), endedAt: text(record.endedAt), endValue: text(record.endValue) });
    }
  }

  let lastEnd: Segment | null = null;
  let open: Segment | null = null;
  for (const segment of segments.values()) {
    const ended = ticks(segment.endedAt);
    if (ended !== null && segment.endValue !== null) {
      const best = ticks(lastEnd?.endedAt ?? null);
      if (best === null || ended > best) {
        lastEnd = segment;
      }
    } else if (segment.startedAt !== null && segment.startValue !== null && segment.operatorId !== null) {
      const started = ticks(segment.startedAt);
      const best = ticks(open?.startedAt ?? null);
      if (started !== null && (best === null || started > best)) {
        open = segment;
      }
    }
  }

  const endedAt = ticks(lastEnd?.endedAt ?? null);
  const openStarted = ticks(open?.startedAt ?? null);
  if (open && endedAt !== null && openStarted !== null && openStarted < endedAt) {
    open = null;
  }

  return {
    lastEnd: lastEnd ? { entryId: lastEnd.entryId, value: lastEnd.endValue ?? "", at: lastEnd.endedAt ?? "" } : null,
    open: open ? {
      entryId: open.entryId, version: open.version, operatorId: open.operatorId ?? "", startedAt: open.startedAt ?? "", startValue: open.startValue ?? "",
      shiftDate: open.shiftDate, shiftCode: open.shiftCode,
    } : null,
  };
}

/** {@link meterState} read from the tablet's database. */
export async function readMeterState(db: KarryOfflineDb, meterId: string): Promise<MeterState> {
  const commands = await db.outbox.where("status").anyOf("pending", "waiting", "confirmed").filter((c) => c.type === startCommand || c.type === endCommand).toArray();
  return meterState(await db.meterStates.get(meterId), meterId, commands);
}

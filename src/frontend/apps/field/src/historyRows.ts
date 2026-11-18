import type { OutboxRecord } from "@karry/offline";
import { endCommand, startCommand } from "./meterState";

/** How far back "My history" goes (US-P2-FO-06 AC1); the server keeps the same 14 days of submissions. */
export const historyDays = 14;

/**
 * Where an entry stands as the tablet knows it (US-P2-FO-06 AC1): `queued` (signed, not yet stored by the server),
 * `submitted` (stored; with the flags a controller will look at) or `rejected` (refused by the server, with its code).
 * Approved and corrected come with the review (P2.8).
 */
export type HistoryStatus = "queued" | "submitted" | "rejected";

export interface HistoryRow {
  entryId: string;
  /** Null when the start was signed on another tablet. */
  assetId: string | null;
  startedAt: string | null;
  startValue: string | null;
  endedAt: string | null;
  endValue: string | null;
  status: HistoryStatus;
  /** The code of the refusal, for a rejected entry. */
  code: string | null;
  flags: string[];
  /** The SHA-256 of the latest meter photo, for its thumbnail. */
  photo: string | null;
  /** The start's values, to sign it again after a refusal. */
  start: { assetId: string; startValue: string; fuelStartPercent: number; remarks: string } | null;
}

interface Signed {
  record?: Record<string, unknown>;
  envelope?: { signer?: { userId?: unknown } };
}

const text = (value: unknown): string | null => (typeof value === "string" ? value : null);
const photoHash = (value: unknown) => (typeof value === "string" && value.startsWith("sha256:") ? value.slice(7) : null);

/**
 * The operator's shift entries of the last {@link historyDays} days, newest first, from the commands this tablet
 * signed for them. One row per entry: its start and end, and the worst state of its commands (a refusal shows over
 * everything, then what is still to send).
 */
export function historyRows(commands: readonly OutboxRecord[], operatorId: string, now: Date): HistoryRow[] {
  const since = now.getTime() - historyDays * 24 * 60 * 60 * 1000;
  const rows = new Map<string, HistoryRow>();
  const ordered = [...commands].sort((a, b) => a.sequence - b.sequence);
  for (const command of ordered) {
    const { record, envelope } = command.payload as Signed;
    const entryId = text(record?.rootId);
    if ((command.type !== startCommand && command.type !== endCommand) || !record || entryId === null
      || envelope?.signer?.userId !== operatorId || Date.parse(command.deviceTime) < since) {
      continue;
    }

    const row: HistoryRow = rows.get(entryId) ?? {
      entryId, assetId: null, startedAt: null, startValue: null, endedAt: null, endValue: null, status: "submitted", code: null, flags: [], photo: null, start: null,
    };
    if (command.type === startCommand) {
      row.assetId = text(record.assetId);
      row.startedAt = text(record.startedAt);
      row.startValue = text(record.startValue);
      row.photo = photoHash(record.startPhoto) ?? row.photo;
      row.start = row.assetId === null || row.startValue === null ? null : {
        assetId: row.assetId, startValue: row.startValue, fuelStartPercent: Number(record.fuelStartPercent), remarks: text(record.remarks) ?? "",
      };
    } else {
      row.endedAt = text(record.endedAt);
      row.endValue = text(record.endValue);
      row.photo = photoHash(record.endPhoto) ?? row.photo;
    }

    if (command.status === "rejected") {
      row.status = "rejected";
      row.code = command.code;
    } else if (row.status !== "rejected" && (command.status === "pending" || command.status === "waiting")) {
      row.status = "queued";
    }

    row.flags = [...new Set([...row.flags, ...command.flags])];
    rows.set(entryId, row);
  }

  const time = (row: HistoryRow) => Date.parse(row.startedAt ?? row.endedAt ?? "") || 0;
  return [...rows.values()].sort((a, b) => time(b) - time(a));
}

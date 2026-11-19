import { Dexie, type EntityTable } from "dexie";
import type { DevicePublicKey, PinVerifier } from "@karry/crypto";

/** This tablet once enrolled (one row, id "this"); the keys are CryptoKeys the browser never exports (ADR-0028 §2). */
export interface DeviceRecord {
  id: "this";
  deviceId: string;
  tenantId: string;
  siteId: string;
  privateKey: CryptoKey;
  publicKey: DevicePublicKey;
  pinKey: CryptoKey;
  enrolledAt: string;
  /** Server clock minus device clock, in milliseconds, measured at each successful exchange. */
  clockOffsetMs: number;
  /** The last sequence number given to a command (schema v2); absent before the first command. */
  lastSequence?: number;
}

/** What the server says about how this tablet behaves (US-P1-OFF-04 AC2/AC3). */
export interface SettingsRecord {
  id: "this";
  autoLockMinutes: number;
  offlineSessionHours: number;
}

export interface SiteRow { id: string; code: string; name: string; type: string; timeZone: string }
/** A shift of a pattern version: wall-clock times "HH:mm" (an end before the start ends the next day), days "mon".."sun". */
export interface ShiftRow { code: string; name: string; start: string; end: string; days: string[] }
export interface ShiftPatternRow { id: string; siteId: string; effectiveFrom: string; shifts: ShiftRow[] }
/** A day the site does not work (P2.5): no shift starts on it. `date` is the site's local date "YYYY-MM-DD". */
export interface NonWorkingDayRow { id: string; siteId: string; date: string; name: string }
export interface AssetRow {
  id: string; code: string; name: string; type: string; siteId: string; status: string;
  make: string | null; model: string | null; serialNumber: string | null; plate: string | null;
  /** The code printed on the machine's QR label, null before a label exists. */
  labelCode: string | null;
}
export interface MeterRow { id: string; assetId: string; kind: string; unit: string; status: string }
export interface MaterialRow { id: string; code: string; name: string; kind: string }
export interface FieldUserRow { id: string; employeeId: string; name: string; pin: PinVerifier | null }
/**
 * Where a meter stands (P2.5, FieldOps `meterStates`): the end of its last ended segment, the reference a start is
 * checked against, and its last segment while that one still runs. Values as the records write them ("12482.5").
 */
export interface MeterStateRow {
  id: string;
  lastEnd: { entryId: string; value: string; at: string } | null;
  open: { entryId: string; version: number; operatorId: string; startedAt: string; startValue: string; shiftDate: string | null; shiftCode: string | null } | null;
}

/** An item of a checklist (ADR-0035 §2): its text per language, whether a Not OK stops the machine, whether it needs a photo. */
export interface ChecklistItemRow { code: string; texts: Record<string, string>; critical: boolean; photoOnNotOk: boolean }
/** The newest published checklist of a machine type (P2.6, FieldOps `checklistTemplates`); `required`: answered before every start. */
export interface ChecklistTemplateRow { id: string; assetType: string; version: number; required: boolean; items: ChecklistItemRow[] }
/** An open safety hold (P2.6, FieldOps `safetyHolds`): no start on the machine until a manager releases it. */
export interface SafetyHoldRow { id: string; assetId: string; items: string[]; openedAt: string }

/** An asset as stored: with the normalized words of its code, name, make, model, serial and plate (search index). */
export type StoredAsset = AssetRow & { words: string[] };

export const collections = ["sites", "shiftPatterns", "assets", "meters", "materials", "users", "meterStates", "nonWorkingDays", "checklistTemplates", "safetyHolds"] as const;
export type Collection = (typeof collections)[number];

export interface SyncStateRecord {
  collection: Collection;
  cursor: string | null;
  lastSuccessAt: string | null;
  lastFullAt: string | null;
}

/** A field user's unlock history on this tablet (US-P1-OFF-04): lockout and the 72 h offline window. */
export interface UnlockRecord {
  userId: string;
  /** Server time of the last online unlock on this tablet; offline unlock is refused without one or after 72 h. */
  lastOnlineAt: string | null;
  failures: number;
  lockedUntil: string | null;
}

/**
 * Where a command stands (ADR-0031 §3–§4): `pending` (to upload, also after a retryable rejection), `waiting` (the
 * server holds it until an earlier number arrives), `confirmed` (stored: accepted, flagged or already there) or
 * `rejected` (refused for good; kept on the tablet with its evidence and shown).
 */
export type OutboxStatus = "pending" | "waiting" | "confirmed" | "rejected";

/** A signed command (P2.2). It leaves the outbox only by being confirmed; the record stays as the tablet's history. */
export interface OutboxRecord {
  commandId: string;
  sequence: number;
  type: string;
  payload: unknown;
  deviceTime: string;
  signature: string;
  status: OutboxStatus;
  /** Uploads that ended in a retryable rejection. */
  attempts: number;
  /** The server's last code for it (a rejection), or null. */
  code: string | null;
  /** Why a confirmed command was flagged for review (`conflict`). */
  flags: string[];
  /** When the server last answered about it. */
  answeredAt: string | null;
  /** SHA-256 (hex) of the evidence blobs its payload names. */
  blobs: string[];
}

/**
 * An evidence blob (ADR-0032 §6): the exact bytes whose hash the command signed, until 7 days after every command
 * naming it is confirmed; a small thumbnail stays for the tablet's history.
 */
export interface BlobRecord {
  sha256: string;
  /** Null once purged. */
  content: Uint8Array | null;
  size: number;
  contentType: string;
  thumbnail: Uint8Array | null;
  uploadedAt: string | null;
  /** When the bytes may go (7 days after confirmation); null while a command naming it is not confirmed. */
  purgeAfter: string | null;
}

/**
 * A form an operator has begun and not signed (P2.5, US-P2-FO-01 AC6): one per operator and form, written at every
 * step, never sent. It survives an auto-lock and a closed app; only the same operator reopens it (T-01). `values`
 * belong to the form (the start screen's machine, value, fuel, remarks and photo).
 */
export interface DraftRecord {
  /** `{operatorId}:{form}`. */
  id: string;
  operatorId: string;
  form: string;
  updatedAt: string;
  values: Record<string, unknown>;
}

export type KarryOfflineDb = Dexie & {
  device: EntityTable<DeviceRecord, "id">;
  settings: EntityTable<SettingsRecord, "id">;
  sites: EntityTable<SiteRow, "id">;
  shiftPatterns: EntityTable<ShiftPatternRow, "id">;
  assets: EntityTable<StoredAsset, "id">;
  meters: EntityTable<MeterRow, "id">;
  materials: EntityTable<MaterialRow, "id">;
  users: EntityTable<FieldUserRow, "id">;
  meterStates: EntityTable<MeterStateRow, "id">;
  nonWorkingDays: EntityTable<NonWorkingDayRow, "id">;
  checklistTemplates: EntityTable<ChecklistTemplateRow, "id">;
  safetyHolds: EntityTable<SafetyHoldRow, "id">;
  drafts: EntityTable<DraftRecord, "id">;
  syncState: EntityTable<SyncStateRecord, "collection">;
  unlocks: EntityTable<UnlockRecord, "userId">;
  outbox: EntityTable<OutboxRecord, "commandId">;
  blobs: EntityTable<BlobRecord, "sha256">;
};

/**
 * Opens the tablet's database, schema v6 (ADR-0028 §5; v2 adds the evidence blobs of P2.2, ADR-0032; v3 the meter states, v4 the non-working days and v5 the drafts of P2.5; v6 the checklists and safety holds of P2.6). Indexes serve the offline screens: machines by code, label
 * code, site and words (multi-entry); users by employee ID; meters by machine; the outbox by sequence.
 */
export function openOfflineDb(name = "karry-field"): KarryOfflineDb {
  const db = new Dexie(name) as KarryOfflineDb;
  db.version(1).stores({
    device: "id",
    settings: "id",
    sites: "id, code",
    shiftPatterns: "id, siteId",
    assets: "id, code, labelCode, siteId, *words",
    meters: "id, assetId",
    materials: "id, code",
    users: "id, &employeeId",
    syncState: "collection",
    unlocks: "userId",
    outbox: "commandId, &sequence, status",
  });
  // v2 (P2.2): evidence blobs; outbox records gain their answer (no v1 record exists: v1 never wrote the outbox).
  db.version(2).stores({
    outbox: "commandId, &sequence, status",
    blobs: "sha256, purgeAfter",
  });
  // v3 (P2.5): where each meter stands, for the start and end screens; filled by the next pull.
  db.version(3).stores({
    meterStates: "id",
  });
  // v4 (P2.5): the site's non-working days, so the tablet resolves the shift as the server does.
  db.version(4).stores({
    nonWorkingDays: "id, siteId",
  });
  // v5 (P2.5): forms begun and not signed, per operator.
  db.version(5).stores({
    drafts: "id, operatorId",
  });
  // v6 (P2.6): the newest checklist per machine type and the open safety holds; filled by the next pull.
  db.version(6).stores({
    checklistTemplates: "id, &assetType",
    safetyHolds: "id, assetId",
  });
  return db;
}

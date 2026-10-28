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
}

/** What the server says about how this tablet behaves (US-P1-OFF-04 AC2/AC3). */
export interface SettingsRecord {
  id: "this";
  autoLockMinutes: number;
  offlineSessionHours: number;
}

export interface SiteRow { id: string; code: string; name: string; type: string; timeZone: string }
export interface ShiftPatternRow { id: string; siteId: string; effectiveFrom: string; shifts: { name: string; start: string; end: string }[] }
export interface AssetRow {
  id: string; code: string; name: string; type: string; siteId: string; status: string;
  make: string | null; model: string | null; serialNumber: string | null; plate: string | null;
  /** The code printed on the machine's QR label, null before a label exists. */
  labelCode: string | null;
}
export interface MeterRow { id: string; assetId: string; kind: string; unit: string; status: string }
export interface MaterialRow { id: string; code: string; name: string; nature: string }
export interface FieldUserRow { id: string; employeeId: string; name: string; pin: PinVerifier | null }

/** An asset as stored: with the normalized words of its code, name, make, model, serial and plate (search index). */
export type StoredAsset = AssetRow & { words: string[] };

export const collections = ["sites", "shiftPatterns", "assets", "meters", "materials", "users"] as const;
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

/** A signed command waiting for upload (Phase 2 fills it; the schema is fixed now so v1 never needs migrating for it). */
export interface OutboxRecord {
  commandId: string;
  sequence: number;
  type: string;
  payload: unknown;
  deviceTime: string;
  signature: string;
  status: "pending" | "sent";
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
  syncState: EntityTable<SyncStateRecord, "collection">;
  unlocks: EntityTable<UnlockRecord, "userId">;
  outbox: EntityTable<OutboxRecord, "commandId">;
};

/**
 * Opens the tablet's database, schema v1 (ADR-0028 §5). Indexes serve the offline screens: machines by code, label
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
  return db;
}

// The field tablet's database (Dexie schema v1) and its reference data (ADR-0028 §5).
// Source-first internal package: apps import TypeScript directly, no build step.
export { applyChanges, assetByLabel, lastRefresh, searchAssets, userByEmployeeId, words, type ChangeSet } from "./reference";
export {
  collections, openOfflineDb,
  type AssetRow, type Collection, type DeviceRecord, type FieldUserRow, type KarryOfflineDb, type MaterialRow, type MeterRow, type OutboxRecord,
  type SettingsRecord, type ShiftPatternRow, type SiteRow, type StoredAsset, type SyncStateRecord, type UnlockRecord,
} from "./schema";

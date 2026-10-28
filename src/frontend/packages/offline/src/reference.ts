import { collections, type AssetRow, type Collection, type FieldUserRow, type KarryOfflineDb, type MaterialRow, type MeterRow, type ShiftPatternRow, type SiteRow, type StoredAsset } from "./schema";

interface Rows {
  sites: SiteRow;
  shiftPatterns: ShiftPatternRow;
  assets: AssetRow;
  meters: MeterRow;
  materials: MaterialRow;
  users: FieldUserRow;
}

/** One answer of `GET /api/v1/sync/changes` (ADR-0028 §5): rows to keep, IDs to drop, and where to continue. */
export interface ChangeSet {
  /** A full snapshot replaces each collection it names; a delta only adds, replaces and removes rows. */
  full: boolean;
  cursor: string;
  serverTime: string;
  /** `replace`: the rows are the whole collection (small ones, such as the site's shift patterns), even in a delta. */
  changes: { [C in Collection]?: { upserts: Rows[C][]; removes: string[]; replace?: boolean } };
}

/** Lower case, accents removed, split on anything not a letter or digit: "Pelle CAT-336" → ["pelle", "cat", "336"]. */
export function words(text: string): string[] {
  return text.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().split(/[^\p{L}\p{N}]+/u).filter(Boolean);
}

function stored(asset: AssetRow): StoredAsset {
  const text = [asset.code, asset.name, asset.make, asset.model, asset.serialNumber, asset.plate].filter(Boolean).join(" ");
  return { ...asset, words: [...new Set(words(text))] };
}

/**
 * Applies a change set in one IndexedDB transaction: either the whole answer is kept with its cursor, or nothing is
 * (a tablet that loses power mid-write pulls the same changes again). Rows are applied idempotently, so the 5-minute
 * overlap of the cursor is harmless.
 */
export async function applyChanges(db: KarryOfflineDb, set: ChangeSet): Promise<void> {
  await db.transaction("rw", [...collections.map((c) => db.table(c)), db.syncState], async () => {
    for (const collection of collections) {
      const change = set.changes[collection];
      if (!change) {
        continue;
      }

      const table = db.table(collection);
      if (set.full || change.replace === true) {
        await table.clear();
      }

      const upserts = collection === "assets" ? (change.upserts as AssetRow[]).map(stored) : change.upserts;
      await table.bulkPut(upserts);
      await table.bulkDelete(change.removes);
      const previous = await db.syncState.get(collection);
      await db.syncState.put({
        collection,
        cursor: set.cursor,
        lastSuccessAt: set.serverTime,
        lastFullAt: set.full ? set.serverTime : (previous?.lastFullAt ?? null),
      });
    }
  });
}

/** When the reference data was last refreshed (shown on the home screen), or null before the first download. */
export async function lastRefresh(db: KarryOfflineDb): Promise<string | null> {
  const states = await db.syncState.toArray();
  const times = states.map((s) => s.lastSuccessAt).filter((t): t is string => t !== null).sort();
  return times.length === collections.length ? (times[0] ?? null) : null;
}

/**
 * Machines matching every word typed (prefixes: "pel 33" finds "Pelle Caterpillar 336"), by code first. Reads only
 * IndexedDB: the first word narrows through the multi-entry index, the others filter.
 */
export async function searchAssets(db: KarryOfflineDb, text: string, limit = 50): Promise<StoredAsset[]> {
  const [first, ...rest] = words(text);
  if (!first) {
    return (await db.assets.orderBy("code").limit(limit).toArray());
  }

  const candidates = await db.assets.where("words").startsWith(first).distinct().toArray();
  return candidates
    .filter((asset) => rest.every((word) => asset.words.some((w) => w.startsWith(word))))
    .sort((a, b) => a.code.localeCompare(b.code))
    .slice(0, limit);
}

/** The machine whose QR label carries this code (scan offline, ADR-0028 §5), or undefined. */
export async function assetByLabel(db: KarryOfflineDb, labelCode: string): Promise<StoredAsset | undefined> {
  return db.assets.where("labelCode").equals(labelCode.trim()).first();
}

/** The field user with this employee ID (case and spaces ignored as typed on the tablet), or undefined. */
export async function userByEmployeeId(db: KarryOfflineDb, employeeId: string): Promise<FieldUserRow | undefined> {
  const wanted = employeeId.trim().toUpperCase();
  return (await db.users.where("employeeId").equals(wanted).first())
    ?? (await db.users.filter((u) => u.employeeId.toUpperCase() === wanted).first());
}

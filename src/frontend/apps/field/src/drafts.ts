import type { KarryOfflineDb } from "@karry/offline";

/** A draft older than this is dropped when its form opens: yesterday's values are not today's (P2.5 decision 4). */
export const draftLifetimeMs = 24 * 60 * 60 * 1000;

const key = (operatorId: string, form: string) => `${operatorId}:${form}`;

/**
 * The draft this operator left in this form (US-P2-FO-01 AC6), or null. Drafts are keyed by operator: another person
 * unlocking the tablet never sees them (T-01). One older than {@link draftLifetimeMs} is deleted instead.
 */
export async function readDraft<T extends Record<string, unknown>>(db: KarryOfflineDb, operatorId: string, form: string, now: Date): Promise<T | null> {
  const draft = await db.drafts.get(key(operatorId, form));
  if (!draft) {
    return null;
  }

  if (now.getTime() - Date.parse(draft.updatedAt) > draftLifetimeMs) {
    await db.drafts.delete(draft.id);
    return null;
  }

  return draft.values as T;
}

/** Keeps the form's values as they are now; nothing of a draft is signed or sent. */
export async function saveDraft(db: KarryOfflineDb, operatorId: string, form: string, values: Record<string, unknown>, now: Date): Promise<void> {
  await db.drafts.put({ id: key(operatorId, form), operatorId, form, updatedAt: now.toISOString(), values });
}

/** Drops the draft: the form was signed, or the operator left it on purpose. */
export async function discardDraft(db: KarryOfflineDb, operatorId: string, form: string): Promise<void> {
  await db.drafts.delete(key(operatorId, form));
}

/**
 * Drops every draft older than {@link draftLifetimeMs}, whoever left it (T-17: a lost tablet keeps no old meter
 * photos or values). Run at each unlock, so a draft nobody reopens does not stay.
 */
export async function purgeDrafts(db: KarryOfflineDb, now: Date): Promise<number> {
  const cutoff = new Date(now.getTime() - draftLifetimeMs).toISOString();
  return db.drafts.filter((d) => d.updatedAt < cutoff).delete();
}

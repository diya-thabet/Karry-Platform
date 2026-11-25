import { liveQuery } from "dexie";
import type { KarryOfflineDb, NoticeRow } from "./schema";

/** A person's notices this tablet holds and they have not read here, newest first (ADR-0039 §3). */
export async function unreadNotices(db: KarryOfflineDb, recipientId: string): Promise<NoticeRow[]> {
  const mine = await db.notices.where("recipientId").equals(recipientId).toArray();
  const read = new Set((await db.noticeReads.bulkGet(mine.map((notice) => notice.id))).flatMap((record) => (record ? [record.id] : [])));
  return mine.filter((notice) => !read.has(notice.id)).sort((a, b) => b.createdAt.localeCompare(a.createdAt));
}

/** Follows a person's unread notices as pulls bring new ones and the person reads them; returns the unsubscribe. */
export function watchNotices(db: KarryOfflineDb, recipientId: string, next: (notices: NoticeRow[]) => void): () => void {
  const subscription = liveQuery(() => unreadNotices(db, recipientId)).subscribe({ next, error: () => undefined });
  return () => { subscription.unsubscribe(); };
}

/** The person read the notice on this tablet; it stays read here even when the next pull brings it again. */
export async function markNoticeRead(db: KarryOfflineDb, id: string, now: Date): Promise<void> {
  await db.noticeReads.put({ id, readAt: now.toISOString() });
}

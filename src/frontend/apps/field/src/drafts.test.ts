import "fake-indexeddb/auto";
import { openOfflineDb } from "@karry/offline";
import { discardDraft, draftLifetimeMs, readDraft, saveDraft } from "./drafts";

describe("drafts of a form (US-P2-FO-01 AC6, T-01)", () => {
  it("keeps one draft per operator and form, never shows it to another operator, and drops it on request", async () => {
    const db = openOfflineDb(`drafts-${crypto.randomUUID()}`);
    const at = new Date("2026-11-10T02:00:00Z");

    await saveDraft(db, "u1", "start", { value: "12.5" }, at);
    await saveDraft(db, "u1", "start", { value: "12.6" }, at);

    expect(await readDraft(db, "u1", "start", at)).toEqual({ value: "12.6" });
    expect(await readDraft(db, "u2", "start", at)).toBeNull();
    expect(await readDraft(db, "u1", "end", at)).toBeNull();
    expect(await db.drafts.count()).toBe(1);
    await discardDraft(db, "u1", "start");
    expect(await readDraft(db, "u1", "start", at)).toBeNull();
    await db.delete();
  });

  it("drops a draft older than a day instead of reopening it", async () => {
    const db = openOfflineDb(`drafts-${crypto.randomUUID()}`);
    const at = new Date("2026-11-10T02:00:00Z");
    await saveDraft(db, "u1", "start", { value: "12.5" }, at);

    expect(await readDraft(db, "u1", "start", new Date(at.getTime() + draftLifetimeMs))).toEqual({ value: "12.5" });
    expect(await readDraft(db, "u1", "start", new Date(at.getTime() + draftLifetimeMs + 1))).toBeNull();
    expect(await db.drafts.count()).toBe(0);
    await db.delete();
  });
});

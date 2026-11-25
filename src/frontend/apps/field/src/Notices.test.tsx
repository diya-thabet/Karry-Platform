import "fake-indexeddb/auto";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { applyChanges, openOfflineDb, unreadNotices, type KarryOfflineDb, type NoticeRow } from "@karry/offline";
import { Notices } from "./Notices";

const mine: NoticeRow = { id: "n1", recipientId: "u1", type: "review.rejected", parameters: { reasonCode: "photo_unreadable" }, createdAt: "2026-11-24T10:00:00Z" };
const older: NoticeRow = { ...mine, id: "n0", parameters: { reasonCode: "wrong_reading" }, createdAt: "2026-11-23T10:00:00Z" };
const theirs: NoticeRow = { ...mine, id: "n2", recipientId: "u2" };

async function tablet(notices: NoticeRow[]): Promise<KarryOfflineDb> {
  const db = openOfflineDb(`notices-${crypto.randomUUID()}`);
  await db.notices.bulkPut(notices);
  return db;
}

async function show(db: KarryOfflineDb, userId = "u1") {
  render(<I18nProvider i18n={await createI18n({ language: "fr" })}><Notices db={db} userId={userId} /></I18nProvider>);
}

describe("notices on the tablet (US-P2-NOT-01 AC1, TC-P2-NOT-001, ADR-0039 §3)", () => {
  it("shows the unlocked person their rejections with the reason, newest first, never another person's", async () => {
    await show(await tablet([older, mine, theirs]));

    const region = await screen.findByRole("region", { name: "Pour vous" });
    const lines = region.querySelectorAll("li");
    expect([...lines].map((line) => line.querySelector("span")?.textContent)).toEqual([
      "Une de vos saisies a été refusée : La photo est illisible. Voir Mon historique.",
      "Une de vos saisies a été refusée : Le relevé du compteur est faux. Voir Mon historique.",
    ]);
  });

  it("keeps a notice read on the tablet even when the next pull brings it again, and shows nothing when all are read", async () => {
    const db = await tablet([mine]);
    await show(db);

    fireEvent.click(await screen.findByRole("button", { name: /^OK, j’ai lu : Une de vos saisies a été refusée/ }));
    await waitFor(() => { expect(screen.queryByRole("region", { name: "Pour vous" })).not.toBeInTheDocument(); });

    await applyChanges(db, { full: false, cursor: "c2", serverTime: "2026-11-25T08:00:00Z", changes: { notices: { upserts: [mine], removes: [], replace: true } } });
    expect(await unreadNotices(db, "u1")).toEqual([]);
  });

  it("shows nothing to a person with no notice", async () => {
    await show(await tablet([theirs]));
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(screen.queryByRole("region", { name: "Pour vous" })).not.toBeInTheDocument();
  });
});

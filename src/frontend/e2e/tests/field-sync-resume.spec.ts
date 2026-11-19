import { type Page } from "@playwright/test";
import { expect, test } from "./guard";
import { enrolledTablet, enrollmentCode, field, meterNow, startAndEnd, unlock } from "./fieldTablet";

interface BlobState {
  sha256: string;
  kept: boolean;
  purgeAfter: string | null;
}

/** The tablet's evidence blobs as stored (Dexie database `karry-field`, store `blobs`), read with the raw IndexedDB API. */
async function blobs(tablet: Page): Promise<BlobState[]> {
  return tablet.evaluate(() => new Promise<BlobState[]>((resolve, reject) => {
    const open = indexedDB.open("karry-field");
    open.onerror = () => { reject(open.error ?? new Error("cannot open the database")); };
    open.onsuccess = () => {
      const request = open.result.transaction("blobs", "readonly").objectStore("blobs").getAll();
      request.onerror = () => { reject(request.error ?? new Error("cannot read the blobs")); };
      request.onsuccess = () => {
        const rows = request.result as { sha256: string; content: unknown; purgeAfter: string | null }[];
        resolve(rows.map((b) => ({ sha256: b.sha256, kept: b.content !== null, purgeAfter: b.purgeAfter })));
        open.result.close();
      };
    };
  }));
}

test.describe("an upload cut off, and evidence kept until confirmed (P2.5, ADR-0031, ADR-0032)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-SYNC-002, TC-P2-SYNC-011: the app is closed while the server stores a batch; reopened, it sends again and the entry is stored once; photos stay until confirmed", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, await enrollmentCode(page));

    await context.setOffline(true);
    await startAndEnd(tablet, meterNow());

    // TC-P2-SYNC-011: the photos, strokes and signature images (identical bytes are stored once), all kept, none due.
    const signed = await blobs(tablet);
    expect(signed.length).toBeGreaterThanOrEqual(2);
    expect(signed.every((b) => b.kept && b.purgeAfter === null), "never purged before the command is confirmed").toBe(true);

    // TC-P2-SYNC-002: the batch reaches the server, which stores it, and the app is closed before the answer arrives.
    let batches = 0;
    await tablet.route("**/api/v1/sync/batches", async (route) => {
      batches++;
      await route.fetch();
      await tablet.close();
    });
    await context.setOffline(false);
    await expect.poll(() => batches, { timeout: 120_000 }).toBeGreaterThan(0);
    await expect.poll(() => tablet.isClosed()).toBe(true);

    // Reopened: the outbox still holds both commands; sent again, the server answers "already there" and nothing doubles.
    const reopened = await context.newPage();
    await reopened.goto(field("demo-carriere-b"));
    await unlock(reopened);
    await expect(reopened.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 180_000 });
    await expect(reopened.getByText(/refusée/)).toHaveCount(0);
    await reopened.getByRole("button", { name: "Mon historique" }).click();
    await expect(reopened.getByRole("listitem")).toHaveCount(1);
    await expect(reopened.getByRole("listitem").first()).toContainText("Reçu");

    // TC-P2-SYNC-011, after: confirmed, the bytes stay (7 days) with a purge date, the thumbnails with them.
    const confirmed = await blobs(reopened);
    expect(confirmed).toHaveLength(signed.length);
    expect(confirmed.every((b) => b.kept && b.purgeAfter !== null), "kept, now due for purge in 7 days").toBe(true);
    await context.close();
  });
});

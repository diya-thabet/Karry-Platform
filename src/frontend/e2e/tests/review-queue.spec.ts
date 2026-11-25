import { enrolledTablet, enrollmentCode, meterNow, portal, startAndEnd } from "./fieldTablet";
import { expect, test } from "./guard";

/** The next meter value a tenth of an hour apart, as the tablet types it (one decimal). */
function plus(value: string, hours: number): string {
  return (Number(value) + hours).toFixed(1);
}

test.describe("the controller's review queue (P2.9, ADR-0038)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-VER-010: entries synced from a tablet reach the open queue within 10 s, and the gap shows the photos side by side (demo step 3)", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const code = await enrollmentCode(page);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, code);

    // Offline, two shifts on PEL-01: the second starts an hour above where the first ended, a continuity gap (RULE-FO-01).
    await context.setOffline(true);
    const first = meterNow();
    await startAndEnd(tablet, first);
    await startAndEnd(tablet, plus(first, 1));

    // The site manager has the queue open before the tablet is back online.
    await page.goto(`${portal("demo-carriere-b")}/review`);
    const queue = page.getByRole("table", { name: "Saisies à contrôler" });
    const empty = page.getByText("Rien à contrôler : chaque saisie de vos sites a une décision.");
    await expect(queue.or(empty)).toBeVisible();
    const before = await queue.getByRole("row").count();

    await context.setOffline(false);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 60_000 });
    // TC-P2-VER-010: both entries appear without reloading, within 10 s of the upload (the page asks every 5 s).
    await expect.poll(async () => queue.getByRole("row").count(), { timeout: 10_000, intervals: [500] }).toBe(Math.max(before, 1) + 2);

    // Demo step 3: the gap comes first (flagged), with the previous end beside this start in the panel.
    const gap = queue.getByRole("row").filter({ hasText: "Le début diffère de la fin précédente sur ce compteur" }).first();
    await gap.getByRole("button", { name: "Contrôler la saisie de PEL-01" }).click();
    const panel = page.getByRole("dialog", { name: "Contrôler une saisie" });
    await expect(panel.getByRole("img", { name: "Fin précédente sur ce compteur" })).toBeVisible();
    await expect(panel.getByRole("img", { name: "Compteur au début" })).toBeVisible();
    await expect(panel.getByText("Le début diffère de la fin précédente sur ce compteur")).toBeVisible();
    await context.close();
  });
});

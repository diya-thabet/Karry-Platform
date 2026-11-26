import { enrolledTablet, enrollmentCode, meterNow, portal, shownAs, startAndEnd } from "./fieldTablet";
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

    // Offline, two shifts on PEL-01: the second starts a tenth of an hour above where the first ended, a continuity gap
    // (RULE-FO-01). A tenth only: the meter must stay below where the specs of the next minutes start (meterNow).
    await context.setOffline(true);
    const first = meterNow();
    const second = plus(first, 0.1);
    await startAndEnd(tablet, first);
    await startAndEnd(tablet, second);

    // The site manager has the queue open before the tablet is back online. Other specs of the same minutes signed
    // entries at the same values: count what is there once the page has loaded, then expect one more of each.
    await page.goto(`${portal("demo-carriere-b")}/review`);
    const queue = page.getByRole("table", { name: "Saisies à contrôler" });
    const empty = page.getByText("Rien à contrôler : chaque saisie de vos sites a une décision.");
    await expect(queue.or(empty)).toBeVisible();
    await expect(page.locator("table[aria-busy=true]")).toHaveCount(0);
    const shown = async () => [
      await page.getByRole("row").filter({ hasText: "PEL-01" }).filter({ hasText: shownAs(first) }).count(),
      await page.getByRole("row").filter({ hasText: "PEL-01" }).filter({ hasText: shownAs(second) }).count(),
    ];
    const [firstBefore = 0, secondBefore = 0] = await shown();
    // The page holds the first 50 items in risk order (queueSize): once the other specs have filled it, an unflagged
    // entry ranks below them and is not on the first page, while the flagged one ranks above. Count the room left.
    const rowsBefore = Math.max(0, (await queue.getByRole("row").count()) - 1);
    const roomForBoth = rowsBefore + 2 <= 50;

    await context.setOffline(false);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 60_000 });
    // TC-P2-VER-010: the entries appear without reloading, within 10 s of the upload (the page asks every 5 s): the
    // flagged one always (it ranks first), the unflagged one when the first page still has room for it. On a full page
    // the lowest unflagged rows leave it as new ones come, so only the flagged count is checked then. Other specs sign
    // at the same values in the same minutes (meterNow has six-minute steps), so a row of theirs may come too: at least
    // one more of each, not exactly one.
    const arrived = async () => {
      const [firstNow = 0, secondNow = 0] = await shown();
      return (secondNow > secondBefore) && (!roomForBoth || firstNow > firstBefore);
    };
    await expect.poll(arrived, { timeout: 10_000, intervals: [500] }).toBe(true);

    // Demo step 3: the second entry starts above the first's end: the gap, with the previous end beside this start.
    const gap = queue.getByRole("row").filter({ hasText: "PEL-01" }).filter({ hasText: shownAs(second) }).filter({ hasText: "Le début diffère de la fin précédente sur ce compteur" }).first();
    await expect(gap).toContainText("Le début diffère de la fin précédente sur ce compteur");
    await gap.getByRole("button", { name: "Contrôler la saisie de PEL-01" }).click();
    const panel = page.getByRole("dialog", { name: "Contrôler une saisie" });
    await expect(panel.getByRole("img", { name: "Fin précédente sur ce compteur" })).toBeVisible();
    await expect(panel.getByRole("img", { name: "Compteur au début" })).toBeVisible();
    await expect(panel.getByText("Le début diffère de la fin précédente sur ce compteur")).toBeVisible();
    await context.close();
  });
});

import { enrolledTablet, enrollmentCode, meterNow, portal, startAndEnd } from "./fieldTablet";
import { expect, test } from "./guard";

/** The next meter value a tenth of an hour apart, as the tablet types it (one decimal). */
function plus(value: string, hours: number): string {
  return (Number(value) + hours).toFixed(1);
}

/**
 * A row's "start → end" as the portal writes it in French ("492 135,5 → 492 135,5 h"): thousands apart by any space
 * (Intl uses a narrow no-break space), a decimal comma. Finds this test's entries among those of the other specs.
 */
function shownAs(value: string): RegExp {
  const [whole = "", tenths = "0"] = value.split(".");
  const groups = whole.replace(/\B(?=(\d{3})+(?!\d))/gu, " ").split(" ");
  const number = `${groups.join("\\s?")}(,${tenths})?`;
  return new RegExp(`${number}\\s*→\\s*${number}\\s*h`, "u");
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
    const second = plus(first, 1);
    const ours = page.getByRole("row").filter({ hasText: shownAs(first) }).or(page.getByRole("row").filter({ hasText: shownAs(second) }));
    await expect(ours).toHaveCount(0);

    await context.setOffline(false);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 60_000 });
    // TC-P2-VER-010: both entries appear without reloading, within 10 s of the upload (the page asks every 5 s).
    await expect(ours).toHaveCount(2, { timeout: 10_000 });

    // Demo step 3: the second entry starts above the first's end: the gap, with the previous end beside this start.
    const gap = queue.getByRole("row").filter({ hasText: shownAs(second) });
    await expect(gap).toContainText("Le début diffère de la fin précédente sur ce compteur");
    await gap.getByRole("button", { name: "Contrôler la saisie de PEL-01" }).click();
    const panel = page.getByRole("dialog", { name: "Contrôler une saisie" });
    await expect(panel.getByRole("img", { name: "Fin précédente sur ce compteur" })).toBeVisible();
    await expect(panel.getByRole("img", { name: "Compteur au début" })).toBeVisible();
    await expect(panel.getByText("Le début diffère de la fin précédente sur ce compteur")).toBeVisible();
    await context.close();
  });
});

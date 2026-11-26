import { type Locator, type Page } from "@playwright/test";
import { enrolledTablet, enrollmentCode, meterNow, portal, shownAs, shownRange, signIn, startAndEnd } from "./fieldTablet";
import { expect, test } from "./guard";
import { secondFactor } from "./totp";

// The Phase 2 demo script (plan.md §9.8), steps 4 and 6; steps 1, 2-3 and 5 are field-checklist, review-queue and
// shift-report (`pnpm --filter @karry/e2e demo` runs the six together, P2.12a slice 6).

/** The next meter value a tenth of an hour apart, as the tablet types it (one decimal). */
function plus(value: string, hours: number): string {
  return (Number(value) + hours).toFixed(1);
}

/**
 * Does `act`, answering the sign-in popup a privileged decision may ask for (a recent sign-in, ADR-0037 §9), then
 * waits for `done`.
 */
async function decide(page: Page, username: string, act: () => Promise<void>, done: Locator) {
  const popup = page.waitForEvent("popup", { timeout: 20_000 }).catch(() => null);
  await act();
  const asked = await Promise.race([popup, done.waitFor({ timeout: 20_000 }).then(() => null, () => null)]);
  if (asked) {
    await signIn(asked, username);
    await secondFactor(asked, username);
  }

  await expect(done).toBeVisible({ timeout: 30_000 });
}

test.describe("the Phase 2 demo (plan §9.8, P2.12a)", () => {
  test.use({ permissions: ["camera"] });

  test("demo step 4, TC-P2-VER-003: a controller corrects a start beyond the threshold, a second person approves it, and the ledger keeps before and after", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const tabletContext = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(tabletContext, await enrollmentCode(page));

    // Demo steps 1-2 in short: two shifts on PEL-01, the second typed a tenth of an hour above where the first ended.
    await tabletContext.setOffline(true);
    const first = meterNow();
    const second = plus(first, 0.1);
    await startAndEnd(tablet, first);
    await startAndEnd(tablet, second);
    await tabletContext.setOffline(false);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 60_000 });
    await tabletContext.close();

    // The controller (second factor) sees the gap and corrects the start to the previous end, as the photos show. The
    // signed Δ was 0, so any change is beyond the thresholds: the correction waits for another person.
    const controllerContext = await browser.newContext({ locale: "fr-FR" });
    const controller = await controllerContext.newPage();
    const controllerName = "controller@demo-carriere-b.karry.test";
    await controller.goto(`${portal("demo-carriere-b")}/review`);
    await signIn(controller, controllerName);
    await secondFactor(controller, controllerName);
    const gap = controller.getByRole("row").filter({ hasText: shownAs(second) }).filter({ hasText: "Le début diffère de la fin précédente sur ce compteur" }).first();
    await expect(gap).toBeVisible({ timeout: 30_000 });
    await gap.getByRole("button", { name: "Contrôler la saisie de PEL-01" }).click();
    const panel = controller.getByRole("dialog", { name: "Contrôler une saisie" });
    await panel.getByRole("button", { name: "Corriger" }).click();
    const correction = controller.getByRole("dialog", { name: "Corriger" });
    await correction.getByLabel(/^Début/).fill(first);
    await correction.getByLabel("Pourquoi").fill("La photo du compteur montre la fin du poste précédent.");
    await decide(controller, controllerName, () => correction.getByRole("button", { name: "Corriger" }).click(),
      controller.getByText("Correction enregistrée : elle attend l’approbation d’une autre personne"));
    await controllerContext.close();

    // The site manager, never the corrector, gives the second approval (TC-P2-VER-003); the versions show the change.
    await page.goto(`${portal("demo-carriere-b")}/review`);
    const waiting = page.getByRole("row").filter({ hasText: shownRange(first, second) }).filter({ hasText: "En attente d’une seconde approbation" }).first();
    await expect(waiting).toBeVisible({ timeout: 30_000 });
    await waiting.getByRole("button", { name: "Contrôler la saisie de PEL-01" }).click();
    const review = page.getByRole("dialog", { name: "Contrôler une saisie" });
    await expect(review.getByText(/^Version 1 :/)).toBeVisible();
    await expect(review.getByText(/^Version 2 :/)).toBeVisible();
    await decide(page, "site-manager@demo-carriere-b.karry.test", () => review.getByRole("button", { name: "Donner la seconde approbation" }).click(),
      page.getByText("Saisie approuvée"));

    // The ledger diff: the correction as recorded, before and after, in the company's audit trail.
    await page.goto(`${portal("demo-carriere-b")}/audit`);
    await page.getByLabel("Événement").fill("verification.review_item_corrected.v1");
    await page.getByLabel("Événement").press("Enter");
    const entry = page.getByRole("row").filter({ hasText: "verification.review_item_corrected.v1" }).first();
    await expect(entry).toBeVisible({ timeout: 30_000 });
    await entry.getByRole("button", { name: /^Détails de l’entrée/ }).click();
    const recorded = page.getByRole("dialog", { name: /^Entrée n° / });
    await expect(recorded).toContainText("verification.review_item_corrected.v1");
  });

  test("demo step 6, TC-P2-SIG-002: an entry changed on the tablet after signing is refused by the server, and the tablet says why", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, await enrollmentCode(page));
    await context.setOffline(true);
    await startAndEnd(tablet, meterNow());

    // Malware on the tablet raises the signed end of the shift by 100 h in the outbox (IndexedDB) before it is sent.
    const changed = await tablet.evaluate(async () => {
      const db = await new Promise<IDBDatabase>((resolve, reject) => {
        const request = indexedDB.open("karry-field");
        request.onsuccess = () => { resolve(request.result); };
        request.onerror = () => { reject(new Error("no outbox")); };
      });
      const transaction = db.transaction("outbox", "readwrite");
      const outbox = transaction.objectStore("outbox");
      const commands = await new Promise<{ sequence: number; payload: { record: { endValue?: string } } }[]>((resolve) => {
        const request = outbox.getAll();
        request.onsuccess = () => { resolve(request.result as { sequence: number; payload: { record: { endValue?: string } } }[]); };
      });
      const end = commands.filter((command) => command.payload.record.endValue !== undefined).sort((a, b) => b.sequence - a.sequence)[0];
      if (!end?.payload.record.endValue) return false;
      end.payload.record.endValue = (Number(end.payload.record.endValue) + 100).toFixed(1);
      outbox.put(end);
      await new Promise((resolve) => { transaction.oncomplete = resolve; });
      db.close();
      return true;
    });
    expect(changed, "the end of the shift was in the outbox").toBe(true);

    await context.setOffline(false);
    const outbox = tablet.getByRole("region", { name: "Envoi des saisies" });
    await expect(outbox.getByText("1 saisie refusée : voir pourquoi")).toBeVisible({ timeout: 60_000 });
    await outbox.getByText("1 saisie refusée : voir pourquoi").click();
    await expect(outbox.getByText("La signature de la tablette ne correspond pas. Signalez-le à un responsable.")).toBeVisible();
    await context.close();
  });
});

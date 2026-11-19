import { expect, test } from "./guard";
import { enrolledTablet, enrollmentCode, field, meterNow, startAndEnd, unlock } from "./fieldTablet";

test.describe("a tablet offline for a while (P2.5 done when, ADR-0031, ADR-0032)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-SYNC-014: ten shifts (20 signed commands with photos) queued offline arrive once, with two tabs of the app uploading at the same time", async ({ browser, page }) => {
    test.setTimeout(600_000);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, await enrollmentCode(page));

    // Offline: each shift starts where the last ended (above any earlier test on this meter: see meterNow).
    await context.setOffline(true);
    const value = meterNow();
    for (let shift = 0; shift < 10; shift++) {
      await startAndEnd(tablet, value);
    }

    const outbox = tablet.getByRole("region", { name: "Envoi des saisies" });
    await expect(outbox).toContainText("20 saisies en attente d’envoi");

    // A second tab of the same tablet, unlocked offline (the tablet checks the PIN itself): both upload when the
    // network comes back, one at a time under the outbox's Web Lock, never giving a number twice.
    const second = await context.newPage();
    await second.goto(field("demo-carriere-b"));
    await unlock(second);
    await expect(second.getByRole("region", { name: "Envoi des saisies" })).toContainText("20 saisies en attente d’envoi");

    await context.setOffline(false);
    for (const page of [tablet, second]) {
      await expect(page.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 180_000 });
      await expect(page.getByText(/refusée/)).toHaveCount(0);
    }

    await tablet.getByRole("button", { name: "Mon historique" }).click();
    const received = tablet.getByRole("listitem").filter({ hasText: "Reçu" });
    await expect(received).toHaveCount(10);
    await expect(tablet.getByRole("listitem").filter({ hasText: "Refusé" })).toHaveCount(0);
    await context.close();
  });
});

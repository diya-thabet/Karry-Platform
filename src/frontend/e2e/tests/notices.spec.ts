import { enrolledTablet, enrollmentCode, meterNow, portal, shownAs, startAndEnd } from "./fieldTablet";
import { expect, test } from "./guard";

test.describe("a notice reaches the operator's tablet (P2.10, ADR-0039)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-NOT-001: a controller rejects an entry in the portal and its operator reads why on the tablet after the next pull", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const code = await enrollmentCode(page);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, code);

    // The operator signs a shift online: it is sent at once and reaches the review queue.
    const value = meterNow();
    await startAndEnd(tablet, value);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 60_000 });

    // The site manager rejects it: the meter on the photo does not match.
    await page.goto(`${portal("demo-carriere-b")}/review`);
    const row = page.getByRole("table", { name: "Saisies à contrôler" }).getByRole("row").filter({ hasText: "PEL-01" }).filter({ hasText: shownAs(value) }).first();
    await expect(row).toBeVisible({ timeout: 30_000 });
    await row.getByRole("button", { name: "Contrôler la saisie de PEL-01" }).click();
    await page.getByRole("dialog", { name: "Contrôler une saisie" }).getByRole("button", { name: "Refuser" }).click();
    const rejection = page.getByRole("dialog", { name: "Refuser" });
    await rejection.getByLabel("Ce qui ne va pas").fill("Le compteur sur la photo ne correspond pas au relevé.");
    await rejection.getByRole("button", { name: "Refuser" }).click();
    // The toast shows the text and announces it again for screen readers (aria-live): the visible one, exactly.
    await expect(page.getByText("Saisie refusée : le conducteur voit pourquoi à la prochaine synchronisation", { exact: true })).toBeVisible();

    // The rejection goes through the outbox to a notice (Workers); the tablet's next pull brings it to its operator.
    const notices = tablet.getByRole("region", { name: "Pour vous" });
    await expect(async () => {
      await tablet.getByRole("button", { name: "Actualiser" }).click();
      await expect(notices).toContainText("Une de vos saisies a été refusée : Le relevé du compteur est faux.", { timeout: 5_000 });
    }).toPass({ timeout: 120_000 });
    await context.close();
  });
});

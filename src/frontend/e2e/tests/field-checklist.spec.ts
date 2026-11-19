import { expect, test } from "./guard";
import { enrolledTablet, enrollmentCode, portal } from "./fieldTablet";

test.describe("a pre-start checklist from the tablet to the portal (P2.6, ADR-0035)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-FO-004, TC-P2-FO-014: offline, a critical Not OK holds the truck; online, the hold reaches the portal, a manager releases it, and the tablet may start it again", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, await enrollmentCode(page));

    // Offline: the truck's checklist is required (demo seed); its brakes are answered Not OK, with the photo they ask for.
    await context.setOffline(true);
    await tablet.getByRole("button", { name: "Commencer mon poste" }).click();
    await tablet.getByLabel(/Code, nom ou étiquette/).fill("tomb");
    await tablet.getByRole("list", { name: "Machines trouvées" }).getByRole("button", { name: /^TOMB-01/ }).click();
    await tablet.getByRole("button", { name: "Choisir cette machine" }).click();
    const checklist = tablet.getByRole("region", { name: "Check-list de démarrage" });
    // The demo truck checklist has four points; `all()` does not wait, so wait for them first.
    await expect(checklist.getByRole("group")).toHaveCount(4);
    await expect(checklist.getByRole("button", { name: "Passer la check-list" })).toHaveCount(0);
    for (const group of await checklist.getByRole("group").all()) {
      const brakes = (await group.getAttribute("aria-label"))?.startsWith("Freins") === true;
      await group.getByRole("button", { name: brakes ? "Non conforme" : "Conforme", exact: true }).click();
    }

    await checklist.getByRole("button", { name: "Photographier le problème" }).click();
    await tablet.getByRole("button", { name: "Prendre la photo" }).click();
    await tablet.getByRole("button", { name: "Garder cette photo" }).click();
    await checklist.getByRole("button", { name: "Signer la check-list" }).click();
    await expect(tablet.getByText(/^Cet engin est immobilisé comme dangereux/)).toBeVisible();
    await expect(tablet.getByRole("button", { name: "Photographier le compteur" })).toHaveCount(0);

    // Back online: the checklist goes up; the portal lists the held truck, and its site manager releases it with a reason.
    await tablet.getByRole("button", { name: "Annuler le début de poste" }).click();
    await context.setOffline(false);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 120_000 });
    await page.goto(`${portal("demo-carriere-b")}/assets/holds`);
    const row = page.getByRole("row").filter({ hasText: "TOMB-01" });
    await expect(row).toContainText("brakes");
    await row.getByRole("button", { name: "Lever" }).click();
    const dialog = page.getByRole("dialog", { name: "Lever l’immobilisation de TOMB-01" });
    await dialog.getByRole("textbox", { name: "Motif" }).fill("Plaquettes changées, freins essayés en charge");
    await dialog.getByRole("button", { name: "Lever l’immobilisation" }).click();
    await expect(page.getByText("Aucun engin immobilisé")).toBeVisible();

    // The next pull drops the hold: the operator may start the truck (the checklist signed a moment ago still counts).
    await tablet.getByRole("button", { name: "Actualiser" }).click();
    await expect(tablet.getByRole("region", { name: "Synchronisation" }).getByText("À jour", { exact: true })).toBeVisible();
    await tablet.getByRole("button", { name: "Commencer mon poste" }).click();
    await tablet.getByLabel(/Code, nom ou étiquette/).fill("tomb");
    await tablet.getByRole("list", { name: "Machines trouvées" }).getByRole("button", { name: /^TOMB-01/ }).click();
    await tablet.getByRole("button", { name: "Choisir cette machine" }).click();
    await expect(tablet.getByRole("button", { name: "Photographier le compteur" })).toBeVisible();
    await expect(tablet.getByText(/^Cet engin est immobilisé/)).toHaveCount(0);
    await context.close();
  });
});

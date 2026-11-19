import { type Locator, type Page } from "@playwright/test";
import { meterNow } from "./fieldTablet";
import { expect, test } from "./guard";

// Demo data of `Karry.Host seed-demo` (local stack only), as in field-enrollment.spec.ts.
const password = process.env.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only";
const pin = process.env.KARRY_DEMO_PIN ?? "804613";
const portal = (slug: string) => `http://${slug}.localhost:5173`;
const field = (slug: string) => `http://${slug}.localhost:${process.env.KARRY_E2E_FIELD_PORT ?? "5174"}`;

async function signIn(page: Page, username: string) {
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(password);
  await page.locator("#kc-login").click();
}

/** A finger on the pad: two strokes of 31 points across it, above the pad's minimums (40 points in all, TC-P2-SIG-008). */
async function sign(tablet: Page) {
  const pad = tablet.getByLabel("Zone de signature");
  const box = await pad.boundingBox();
  if (!box) throw new Error("no signature pad");
  for (const y of [0.35, 0.65]) {
    await tablet.mouse.move(box.x + box.width * 0.15, box.y + box.height * y);
    await tablet.mouse.down();
    for (let step = 1; step <= 30; step++) {
      await tablet.mouse.move(box.x + box.width * (0.15 + step * 0.023), box.y + box.height * (y + (step % 2 === 0 ? 0.08 : -0.08)));
    }
    await tablet.mouse.up();
  }

  await tablet.getByRole("button", { name: "Confirmer" }).click();
}

/** The meter photo through the app's own camera (Chromium's fake camera stream; no file input exists: TC-P2-FO-002). */
async function photograph(tablet: Page) {
  await expect(tablet.locator("input[type=file]")).toHaveCount(0);
  await tablet.getByRole("button", { name: "Photographier le compteur" }).click();
  await tablet.getByRole("button", { name: "Prendre la photo" }).click();
  await tablet.getByRole("button", { name: "Garder cette photo" }).click();
  await expect(tablet.getByText("Photo du compteur prise")).toBeVisible();
}

test.describe("a shift entry from the tablet to the portal (P2.5, ADR-0033, ADR-0034)", () => {
  test.use({ permissions: ["camera"] });

  test("offline, an operator starts and ends a shift with photos and signatures; back online it arrives once, and the portal verifies its signature", async ({ browser, page }) => {
    test.setTimeout(240_000);
    // A tablet of company B's quarry (CAR-KAK, with the excavator PEL-01), enrolled by its site manager.
    await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    await page.getByRole("button", { name: "Ajouter une tablette" }).click();
    await page.getByRole("combobox", { name: "Site" }).click();
    await page.getByRole("option", { name: /^CAR-KAK · / }).click();
    await page.getByRole("button", { name: "Créer le code" }).click();
    const code = (await page.getByLabel("Code à saisir sur la tablette").textContent())?.trim() ?? "";
    await page.keyboard.press("Escape");

    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await context.newPage();
    await tablet.goto(`${field("demo-carriere-b")}/enroll#${code}`);
    await tablet.getByLabel(/Nom de la tablette/).fill(`Poste ${String(Date.now())}`);
    await tablet.getByRole("button", { name: "Enregistrer la tablette" }).click();
    await tablet.getByLabel(/Matricule/).fill("DEMO-OPERATOR");
    await tablet.getByLabel(/Code PIN/).fill(pin);
    await tablet.getByRole("button", { name: "Déverrouiller", exact: true }).click();
    const sync = tablet.getByRole("region", { name: "Synchronisation" });
    await expect(sync.getByText("À jour", { exact: true })).toBeVisible();
    await tablet.evaluate(async () => { await navigator.serviceWorker.ready; });

    // Offline from here: the start and the end are signed on the tablet only.
    await context.setOffline(true);
    // Above any end of earlier tests and runs on this meter (see meterNow); a large jump needs a reason, given in the remarks.
    const value = meterNow();
    await tablet.getByRole("button", { name: "Commencer mon poste" }).click();
    await tablet.getByLabel(/Code, nom ou étiquette/).fill("pel");
    await tablet.getByRole("list", { name: "Machines trouvées" }).getByRole("button", { name: /^PEL-01/ }).click();
    await tablet.getByRole("button", { name: "Choisir cette machine" }).click();
    await expect(tablet.getByText(/^Poste : |^Aucun poste du site/)).toBeVisible();
    await photograph(tablet);
    await tablet.getByLabel(/Valeur de début/).fill(value);
    await tablet.getByLabel(/Raison de l’écart|Remarques/).fill("Essai de bout en bout");
    await tablet.getByRole("button", { name: "Vérifier et signer" }).click();
    await sign(tablet);
    await expect(tablet.getByText("Poste commencé. Il sera envoyé à la prochaine synchronisation.")).toBeVisible();

    const active: Locator = tablet.getByRole("region", { name: "Mon poste en cours" });
    await expect(active).toContainText("PEL-01");
    await active.getByRole("button", { name: "Terminer mon poste" }).click();
    await photograph(tablet);
    await tablet.getByLabel(/Valeur de fin/).fill(value);
    await expect(tablet.getByText("Δ : 0.0 h")).toBeVisible();
    await tablet.getByRole("button", { name: "Vérifier et signer" }).click();
    await sign(tablet);
    await expect(tablet.getByText("Poste terminé. Il sera envoyé à la prochaine synchronisation.")).toBeVisible();
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" })).toContainText("2 saisies en attente d’envoi");

    // Back online: the photos, the signature strokes and images, then the commands go up, once.
    await context.setOffline(false);
    await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 60_000 });
    await tablet.getByRole("button", { name: "Mon historique" }).click();
    const entry = tablet.getByRole("listitem").filter({ hasText: "PEL-01" }).first();
    await expect(entry).toContainText("Reçu");
    await expect(entry).toContainText(`Début : ${value}`);
    await expect(entry).toContainText(`Fin : ${value}`);

    // The portal: the received envelope of the end is in the audit trail, verified once the server has countersigned
    // it (the countersign job runs every 30 seconds in Karry.Workers).
    await page.goto(`${portal("demo-carriere-b")}/audit`);
    await page.getByLabel("Événement").fill("trust.envelope_received.v1");
    await page.getByRole("button", { name: "Appliquer les filtres" }).click();
    await expect.poll(async () => {
      await page.getByRole("button", { name: /^Détails de l’entrée n° / }).first().click();
      const verified = await page.getByText("Vérifiée", { exact: true }).isVisible();
      await page.keyboard.press("Escape");
      if (!verified) {
        await page.getByRole("button", { name: "Appliquer les filtres" }).click();
      }

      return verified;
    }, { timeout: 120_000, intervals: [5_000] }).toBe(true);
    await context.close();
  });
});

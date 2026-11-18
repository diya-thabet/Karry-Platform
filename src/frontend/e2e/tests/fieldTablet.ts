import { type BrowserContext, type Page } from "@playwright/test";
import { expect } from "./guard";

// Steps of the field tablet shared by the browser tests (demo data of `Karry.Host seed-demo`, local stack only).
export const password = process.env.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only";
export const pin = process.env.KARRY_DEMO_PIN ?? "804613";
export const portal = (slug: string) => `http://${slug}.localhost:5173`;
export const field = (slug: string) => `http://${slug}.localhost:${process.env.KARRY_E2E_FIELD_PORT ?? "5174"}`;

export async function signIn(page: Page, username: string) {
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(password);
  await page.locator("#kc-login").click();
}

/** An enrollment code for company B's quarry CAR-KAK (with the excavator PEL-01), made by its site manager. */
export async function enrollmentCode(page: Page): Promise<string> {
  await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
  await signIn(page, "site-manager@demo-carriere-b.karry.test");
  await page.getByRole("button", { name: "Ajouter une tablette" }).click();
  await page.getByRole("combobox", { name: "Site" }).click();
  await page.getByRole("option", { name: /^CAR-KAK · / }).click();
  await page.getByRole("button", { name: "Créer le code" }).click();
  const code = (await page.getByLabel("Code à saisir sur la tablette").textContent())?.trim() ?? "";
  await page.keyboard.press("Escape");
  return code;
}

export async function unlock(tablet: Page) {
  await tablet.getByLabel(/Matricule/).fill("DEMO-OPERATOR");
  await tablet.getByLabel(/Code PIN/).fill(pin);
  await tablet.getByRole("button", { name: "Déverrouiller", exact: true }).click();
}

/** A tablet enrolled with `code`, unlocked by DEMO-OPERATOR, its data downloaded and its service worker ready. */
export async function enrolledTablet(context: BrowserContext, code: string): Promise<Page> {
  const tablet = await context.newPage();
  await tablet.goto(`${field("demo-carriere-b")}/enroll#${code}`);
  await tablet.getByLabel(/Nom de la tablette/).fill(`Tablette ${String(Date.now())}`);
  await tablet.getByRole("button", { name: "Enregistrer la tablette" }).click();
  await unlock(tablet);
  await expect(tablet.getByRole("region", { name: "Synchronisation" }).getByText("À jour", { exact: true })).toBeVisible();
  await tablet.evaluate(async () => { await navigator.serviceWorker.ready; });
  return tablet;
}

/** A finger on the pad: two strokes of 31 points across it, above the pad's minimums (40 points in all, TC-P2-SIG-008). */
export async function sign(tablet: Page) {
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

/** The meter photo through the app's own camera (Chromium's fake camera stream). */
export async function photograph(tablet: Page) {
  await tablet.getByRole("button", { name: "Photographier le compteur" }).click();
  await tablet.getByRole("button", { name: "Prendre la photo" }).click();
  await tablet.getByRole("button", { name: "Garder cette photo" }).click();
  await expect(tablet.getByText("Photo du compteur prise")).toBeVisible();
}

/** Starts and ends a shift on PEL-01 at `value` (Δ 0), each step photographed and signed. */
export async function startAndEnd(tablet: Page, value: string) {
  await tablet.getByRole("button", { name: "Commencer mon poste" }).click();
  await tablet.getByLabel(/Code, nom ou étiquette/).fill("pel");
  await tablet.getByRole("list", { name: "Machines trouvées" }).getByRole("button", { name: /^PEL-01/ }).click();
  await tablet.getByRole("button", { name: "Choisir cette machine" }).click();
  await photograph(tablet);
  await tablet.getByLabel(/Valeur de début/).fill(value);
  await tablet.getByLabel(/Raison de l’écart|Remarques/).fill("Essai de bout en bout");
  await tablet.getByRole("button", { name: "Vérifier et signer" }).click();
  await sign(tablet);
  await expect(tablet.getByText("Poste commencé. Il sera envoyé à la prochaine synchronisation.")).toBeVisible();

  await tablet.getByRole("region", { name: "Mon poste en cours" }).getByRole("button", { name: "Terminer mon poste" }).click();
  await photograph(tablet);
  await tablet.getByLabel(/Valeur de fin/).fill(value);
  await tablet.getByRole("button", { name: "Vérifier et signer" }).click();
  await sign(tablet);
  await expect(tablet.getByText("Poste terminé. Il sera envoyé à la prochaine synchronisation.")).toBeVisible();
}

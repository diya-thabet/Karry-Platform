import { expect, test, type Page } from "@playwright/test";

// Demo users from `Karry.Host seed-demo` (local stack only): <role>@<slug>.karry.test.
const password = process.env.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only";
const portal = (slug: string) => `http://${slug}.localhost:5173`;
// The built field app (vite preview, with its service worker); KARRY_E2E_FIELD_PORT runs it beside a dev server.
const field = (slug: string) => `http://${slug}.localhost:${process.env.KARRY_E2E_FIELD_PORT ?? "5174"}`;

// A field user of the demo (DEMO-<ROLE>, company-wide), unlocking the shared tablet with the demo PIN.
const operator = "DEMO-OPERATOR";
const pin = process.env.KARRY_DEMO_PIN ?? "804613";

async function unlock(tablet: Page, employeeId: string, code: string) {
  await expect(tablet.getByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeVisible();
  await tablet.getByLabel(/Matricule/).fill(employeeId);
  await tablet.getByLabel(/Code PIN/).fill(code);
  await tablet.getByRole("button", { name: "Déverrouiller", exact: true }).click();
}

async function signIn(page: Page, username: string) {
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(password);
  await page.locator("#kc-login").click();
}

/** A new enrollment code from the portal's Site tablets screen (signed in as the site manager of company B). */
async function newCode(page: Page) {
  await page.getByRole("button", { name: "Ajouter une tablette" }).click();
  await page.getByRole("combobox", { name: "Site" }).click();
  await page.getByRole("option").first().click();
  await page.getByRole("button", { name: "Créer le code" }).click();
  const code = (await page.getByLabel("Code à saisir sur la tablette").textContent())?.trim() ?? "";
  await page.keyboard.press("Escape");
  return code;
}

async function enroll(tablet: Page, code: string, name: string) {
  await tablet.goto(`${field("demo-carriere-b")}/enroll#${code}`);
  await tablet.getByLabel(/Nom de la tablette/).fill(name);
  await tablet.getByRole("button", { name: "Enregistrer la tablette" }).click();
}

test.describe("site tablets (US-P1-OFF-02, ADR-0028)", () => {
  test("TC-P1-OFF-001/003, OFF-04: a site manager adds a tablet with a code, the tablet enrols, an operator unlocks it, it downloads its site's data, keeps it, unlocks and finds a machine offline, and is listed", async ({ browser, page }) => {
    const name = `Pont-bascule ${String(Date.now())}`;
    await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    await page.getByRole("button", { name: "Ajouter une tablette" }).click();
    await page.getByRole("combobox", { name: "Site" }).click();
    // The demo quarry of company B, whose machines include the excavator PEL-01.
    await page.getByRole("option", { name: /^CAR-KAK · / }).click();
    await page.getByRole("button", { name: "Créer le code" }).click();
    const code = (await page.getByLabel("Code à saisir sur la tablette").textContent())?.trim() ?? "";
    expect(code).toMatch(/^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$/);

    // The tablet: its own browser, no sign-in, opened from the link of the QR code.
    const context = await browser.newContext({ locale: "fr-FR" });
    const tablet = await context.newPage();
    await tablet.goto(`${field("demo-carriere-b")}/enroll#${code}`);
    await expect(tablet.getByRole("heading", { level: 1, name: "Enregistrer cette tablette" })).toBeVisible();
    await expect(tablet.getByLabel(/Code d’enregistrement/)).toHaveValue(code);
    expect(new URL(tablet.url()).hash, "the code leaves the address bar once read").toBe("");
    await tablet.getByLabel(/Nom de la tablette/).fill(name);
    await tablet.getByRole("button", { name: "Enregistrer la tablette" }).click();

    // The first unlock of a user on a tablet is checked by the server.
    await unlock(tablet, operator, pin);
    await expect(tablet.getByRole("heading", { level: 1, name: "Karry Terrain" })).toBeVisible();
    await expect(tablet.getByText(`(${operator})`)).toBeVisible();
    const sync = tablet.getByRole("region", { name: "Synchronisation" });
    await expect(sync.getByText("À jour", { exact: true })).toBeVisible();
    await expect(sync.getByText(/^Site : /)).toBeVisible();
    await expect(sync.getByText(/^Données mises à jour le /)).toBeVisible();
    const kept = (await sync.getByText(/disponibles hors ligne$/).textContent()) ?? "";
    expect(kept).toMatch(/^\d+ machines? et \d+ opérateurs? disponibles hors ligne$/);

    // Offline: the app and its data come from the tablet (service worker and IndexedDB), the key stays enrolled; the
    // reloaded app is locked and the tablet itself checks the PIN, against the verifier it downloaded.
    await tablet.evaluate(async () => { await navigator.serviceWorker.ready; });
    await context.setOffline(true);
    await tablet.reload();
    await unlock(tablet, operator, "000000");
    await expect(tablet.getByRole("alert"), "a wrong PIN is refused offline too").toHaveText("Matricule ou code PIN incorrect.");
    await unlock(tablet, operator.toLowerCase(), pin);
    await expect(tablet.getByRole("heading", { level: 1, name: "Karry Terrain" }), "enrolled for good: the key is kept on the tablet").toBeVisible();
    await expect(sync.getByText("Hors ligne", { exact: true })).toBeVisible();
    await expect(sync.getByText(kept)).toBeVisible();

    // Still offline: a machine is found on the tablet by its typed code (no camera in the test browser).
    await tablet.getByRole("button", { name: "Trouver une machine" }).click();
    await tablet.getByLabel(/Code, nom ou étiquette/).fill("pel");
    await tablet.getByRole("list", { name: "Machines trouvées" }).getByRole("button", { name: /^PEL-01/ }).click();
    await expect(tablet.getByRole("region", { name: "Machine choisie" })).toContainText("Pelle Komatsu PC350");
    await tablet.getByRole("button", { name: "Retour à l’accueil" }).click();
    await context.setOffline(false);

    await tablet.getByRole("button", { name: "Verrouiller" }).click();
    await expect(tablet.getByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeVisible();

    await page.reload();
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toContainText("Active");
  });

  test("TC-P1-OFF-002: a code that was already used is refused on a second tablet", async ({ browser, page }) => {
    await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    const code = await newCode(page);

    for (const [index, expected] of [[1, "Déverrouiller la tablette"], [2, null]] as const) {
      const tablet = await (await browser.newContext({ locale: "fr-FR" })).newPage();
      await enroll(tablet, code, `Tablette ${String(index)}`);
      if (expected) {
        await expect(tablet.getByRole("heading", { level: 1, name: expected })).toBeVisible();
      } else {
        await expect(tablet.getByRole("alert")).toHaveText("Ce code a expiré ou a déjà servi. Demandez un nouveau code.");
      }
    }
  });

  test("TC-P1-OFF-008: after the browser's site data is cleared, the tablet asks to be enrolled again and a new code works", async ({ browser, page }) => {
    const stamp = String(Date.now());
    await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    const context = await browser.newContext({ locale: "fr-FR" });
    const tablet = await context.newPage();
    await enroll(tablet, await newCode(page), `Avant ${stamp}`);
    await expect(tablet.getByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeVisible();

    // What "Clear site data" does in Chrome's settings: storage, IndexedDB (the device key with it) and service worker.
    const cdp = await context.newCDPSession(tablet);
    await cdp.send("Storage.clearDataForOrigin", { origin: new URL(tablet.url()).origin, storageTypes: "all" });
    await tablet.goto(field("demo-carriere-b"));
    await expect(tablet.getByRole("heading", { level: 1, name: "Enregistrer cette tablette" }), "no key left: enrollment again").toBeVisible();

    await enroll(tablet, await newCode(page), `Après ${stamp}`);
    await expect(tablet.getByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeVisible();
    await unlock(tablet, operator, pin);
    await expect(tablet.getByRole("heading", { level: 1, name: "Karry Terrain" })).toBeVisible();

    await page.reload();
    for (const name of [`Avant ${stamp}`, `Après ${stamp}`]) {
      await expect(page.getByRole("row").filter({ hasText: name }), "each enrollment is its own tablet, to revoke if lost").toContainText("Active");
    }
  });
});

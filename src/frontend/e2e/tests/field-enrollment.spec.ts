import { expect, test, type Page } from "@playwright/test";

// Demo users from `Karry.Host seed-demo` (local stack only): <role>@<slug>.karry.test.
const password = process.env.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only";
const portal = (slug: string) => `http://${slug}.localhost:5173`;
// The built field app (vite preview, with its service worker); KARRY_E2E_FIELD_PORT runs it beside a dev server.
const field = (slug: string) => `http://${slug}.localhost:${process.env.KARRY_E2E_FIELD_PORT ?? "5174"}`;

async function signIn(page: Page, username: string) {
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(password);
  await page.locator("#kc-login").click();
}

test.describe("site tablets (US-P1-OFF-02, ADR-0028)", () => {
  test("TC-P1-OFF-001/003: a site manager adds a tablet with a code, the tablet enrols, downloads its site's data, keeps it offline and is listed", async ({ browser, page }) => {
    const name = `Pont-bascule ${String(Date.now())}`;
    await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    await page.getByRole("button", { name: "Ajouter une tablette" }).click();
    await page.getByRole("combobox", { name: "Site" }).click();
    await page.getByRole("option").first().click();
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

    await expect(tablet.getByRole("heading", { level: 1, name: "Karry Terrain" })).toBeVisible();
    const sync = tablet.getByRole("region", { name: "Synchronisation" });
    await expect(sync.getByText("À jour", { exact: true })).toBeVisible();
    await expect(sync.getByText(/^Site : /)).toBeVisible();
    await expect(sync.getByText(/^Données mises à jour le /)).toBeVisible();
    const kept = (await sync.getByText(/disponibles hors ligne$/).textContent()) ?? "";
    expect(kept).toMatch(/^\d+ machines? et \d+ opérateurs? disponibles hors ligne$/);

    // Offline: the app and its data come from the tablet (service worker and IndexedDB), the key stays enrolled.
    await tablet.evaluate(async () => { await navigator.serviceWorker.ready; });
    await context.setOffline(true);
    await tablet.reload();
    await expect(tablet.getByRole("heading", { level: 1, name: "Karry Terrain" }), "enrolled for good: the key is kept on the tablet").toBeVisible();
    await expect(sync.getByText("Hors ligne", { exact: true })).toBeVisible();
    await expect(sync.getByText(kept)).toBeVisible();
    await context.setOffline(false);

    await page.reload();
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toContainText("Active");
  });

  test("TC-P1-OFF-002: a code that was already used is refused on a second tablet", async ({ browser, page }) => {
    await page.goto(`${portal("demo-carriere-b")}/settings/tablets`);
    await signIn(page, "site-manager@demo-carriere-b.karry.test");
    await page.getByRole("button", { name: "Ajouter une tablette" }).click();
    await page.getByRole("combobox", { name: "Site" }).click();
    await page.getByRole("option").first().click();
    await page.getByRole("button", { name: "Créer le code" }).click();
    const code = (await page.getByLabel("Code à saisir sur la tablette").textContent())?.trim() ?? "";

    for (const [index, expected] of [[1, "Karry Terrain"], [2, null]] as const) {
      const tablet = await (await browser.newContext({ locale: "fr-FR" })).newPage();
      await tablet.goto(`${field("demo-carriere-b")}/enroll#${code}`);
      await tablet.getByLabel(/Nom de la tablette/).fill(`Tablette ${String(index)}`);
      await tablet.getByRole("button", { name: "Enregistrer la tablette" }).click();
      if (expected) {
        await expect(tablet.getByRole("heading", { level: 1, name: expected })).toBeVisible();
      } else {
        await expect(tablet.getByRole("alert")).toHaveText("Ce code a expiré ou a déjà servi. Demandez un nouveau code.");
      }
    }
  });
});

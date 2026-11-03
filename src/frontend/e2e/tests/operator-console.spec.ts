import { type Page } from "@playwright/test";
import { expect, test } from "./guard";
import { secondFactor } from "./totp";

// Demo accounts from `Karry.Host seed-demo` (local stack only). The browser tests have their own operator, so a person
// showing the console keeps their own authenticator on operator@karry.test.
const password = process.env.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only";
const platform = "http://localhost:5173";
const portal = (slug: string) => `http://${slug}.localhost:5173`;
const operator = "e2e-operator@karry.test";
const companyAdmin = "tenant-admin@demo-carriere-b.karry.test";

async function signIn(page: Page, username: string) {
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(password);
  await page.locator("#kc-login").click();
  await secondFactor(page, username);
}

/**
 * The operator in the console. Right after enrolling the authenticator the token does not count it yet (Keycloak:
 * enrolment is a required action), so the portal asks to sign in again: the pop-up asks for the password and a code.
 */
async function operatorConsole(page: Page) {
  await page.goto(`${platform}/operator`);
  await signIn(page, operator);
  // The loaded list: while it loads the table shows skeleton rows and is busy (UI/UX review UX-12).
  const companies = page.getByRole("table", { name: "Entreprises" }).and(page.locator(":not([aria-busy])"));
  const again = page.getByRole("button", { name: "Se reconnecter" });
  await expect(companies.or(again)).toBeVisible();
  if (await again.isVisible()) {
    const popupOpened = page.waitForEvent("popup");
    await again.click();
    const popup = await popupOpened;
    if (await popup.locator("#username").isVisible()) {
      await popup.locator("#username").fill(operator);
    }

    await popup.locator("#password").fill(password);
    await popup.locator("#kc-login").click();
    await secondFactor(popup, operator);
  }

  await expect(page.getByRole("row", { name: /Carrière Démo B/ })).toBeVisible();
}

test.describe("operator console and support access (US-P1-OPS-03, ADR-0029)", () => {
  // A code is used once per 30-second window: later sign-ins of an account may wait for the next window.
  test.describe.configure({ timeout: 150_000 });

  test("an operator with a second factor creates a company and changes its plan", async ({ page }) => {
    const slug = `e2e-${String(Date.now()).slice(-8)}`;
    await operatorConsole(page);

    await page.getByRole("button", { name: "Nouvelle entreprise" }).click();
    const dialog = page.getByRole("dialog", { name: "Nouvelle entreprise" });
    await dialog.getByLabel("Nom de l’entreprise").fill(`Carrière ${slug}`);
    await dialog.getByLabel("Adresse").fill(slug);
    await dialog.getByRole("button", { name: "Créer l’entreprise" }).click();
    await expect(page.getByText(`Carrière ${slug} créée`, { exact: true })).toBeVisible();

    await page.getByRole("link", { name: `Carrière ${slug}` }).click();
    await expect(page.getByRole("heading", { level: 1, name: `Carrière ${slug}` })).toBeVisible();
    await page.getByRole("combobox", { name: "Offre" }).click();
    await page.getByRole("option", { name: /^business/ }).click();
    await page.getByRole("button", { name: "Enregistrer les conditions" }).click();
    await expect(page.getByText("Conditions enregistrées", { exact: true })).toBeVisible();
    await page.reload();
    await signInIfAsked(page);
    await expect(page.getByRole("combobox", { name: "Offre" })).toContainText("business");
  });

  test("TC-P1-OPS-004: with the company's code the operator looks at its screens under the banner, read only, until ended; the company sees the session and runs the integrity check", async ({ browser, page }) => {
    // The company's administrator creates a support code (Settings › Support access).
    const company = await (await browser.newContext({ locale: "fr-FR" })).newPage();
    await company.goto(`${portal("demo-carriere-b")}/settings/support`);
    await signIn(company, companyAdmin);
    await company.getByRole("button", { name: "Créer un code support" }).click();
    await company.getByRole("dialog").getByRole("button", { name: "Créer le code" }).click();
    const code = ((await company.getByLabel("Code support", { exact: true }).textContent()) ?? "").trim();
    expect(code).toMatch(/^[0-9A-Z]{4}(-[0-9A-Z]{4}){3}$/);

    // The operator opens support on that company with the code read to them.
    await operatorConsole(page);
    await page.getByRole("link", { name: "Carrière Démo B" }).click();
    await page.getByRole("button", { name: "Ouvrir le support" }).click();
    const open = page.getByRole("dialog", { name: "Ouvrir le support" });
    await open.getByLabel("Code support").fill(code);
    await open.getByRole("button", { name: "Ouvrir le support" }).click();

    const banner = page.getByRole("complementary", { name: "Session de support Karry" });
    await expect(banner).toContainText("Support Karry dans Carrière Démo B : consultation seule");
    await page.getByRole("navigation", { name: "Navigation principale" }).getByRole("link", { name: "Sites" }).click();
    await expect(page.getByText("Carrière de Kakoulima", { exact: true })).toBeVisible(); // the company's own data
    await expect(page.getByRole("link", { name: "Nouveau site" })).toHaveCount(0); // support only looks

    await banner.getByRole("button", { name: "Terminer le support" }).click();
    await expect(page.getByText("Session de support terminée", { exact: true })).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: "Carrière Démo B" })).toBeVisible();
    await expect(banner).toHaveCount(0);

    // The company sees who looked, how, and that it is over; then checks its audit trail.
    await company.getByRole("button", { name: "Terminé" }).click();
    await company.reload();
    await signInIfAsked(company);
    const session = company.getByRole("table", { name: "Sessions de support" }).getByRole("row", { name: /E2E Operator/ }).first();
    await expect(session).toContainText("Avec votre code");
    await expect(session).toContainText("Terminée");

    await company.goto(`${portal("demo-carriere-b")}/audit`);
    await signInIfAsked(company);
    await company.getByRole("button", { name: "Vérifier maintenant" }).click();
    await expect(company.getByText("Intact", { exact: true })).toBeVisible();
  });
});

/** A reload loses the in-memory tokens: the Keycloak session signs in again without a form, unless it asks. */
async function signInIfAsked(page: Page) {
  await expect(page.locator("#root").or(page.locator("#username"))).toBeAttached();
  if (await page.locator("#username").isVisible()) {
    throw new Error("The Keycloak session should have signed the user in again without a form.");
  }
}

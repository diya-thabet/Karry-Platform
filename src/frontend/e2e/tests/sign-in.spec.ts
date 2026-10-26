import { expect, test, type Page } from "@playwright/test";

// Demo users from `Karry.Host seed-demo` (local stack only): <role>@<slug>.karry.test.
const password = process.env.KARRY_DEMO_PASSWORD ?? "karry-demo-local-only";
const portal = (slug: string) => `http://${slug}.localhost:5173`;
const field = (slug: string) => `http://${slug}.localhost:5174`;

// Keycloak's form fields keep stable IDs across themes and languages.
async function signIn(page: Page, username: string, secret = password) {
  await page.locator("#username").fill(username);
  await page.locator("#password").fill(secret);
  await page.locator("#kc-login").click();
}

/** Every value in the app's web storage: none may be (or contain) a token. */
async function webStorage(page: Page): Promise<string[]> {
  return page.evaluate(() =>
    [window.localStorage, window.sessionStorage].flatMap((store) => Object.keys(store).map((key) => `${key}=${store.getItem(key) ?? ""}`)),
  );
}

test.describe("sign-in (US-P1-IAM-01, US-P1-IAM-02)", () => {
  test("TC-P1-IAM-001: the portal signs in through Keycloak in French with the tenant's branding, tokens stay in memory", async ({ page }) => {
    await page.goto(portal("demo-carriere-a"));

    await expect(page).toHaveURL(/\/realms\/karry\/protocol\/openid-connect\/auth/);
    await expect(page.locator("html")).toHaveAttribute("lang", "fr");
    await expect(page.getByRole("heading", { name: "Connectez-vous à Karry" })).toBeVisible();
    await expect(page.getByTestId("tenant-name")).toHaveText("Carrière Démo A");
    await expect(page.locator("#kc-login")).toHaveCSS("background-color", "rgb(31, 78, 121)"); // the company's colour (US-P1-TEN-04)

    await signIn(page, "site-manager@demo-carriere-a.karry.test");

    await expect(page).toHaveURL(`${portal("demo-carriere-a")}/`);
    await expect(page.getByText("Connecté : Demo SiteManager")).toBeVisible();
    const stored = await webStorage(page);
    expect(stored.filter((entry) => /eyJ|refresh_token|access_token|id_token/.test(entry)), "no token in localStorage or sessionStorage").toEqual([]);

    // A reload loses the in-memory tokens: the Keycloak session signs the user in again without a form.
    await page.reload();
    await expect(page.getByText("Connecté : Demo SiteManager")).toBeVisible();

    await page.getByRole("button", { name: "Se déconnecter" }).click();
    await expect(page.getByRole("heading", { name: "Connectez-vous à Karry" })).toBeVisible();
  });

  test("TC-P1-IAM-001: the login page follows the app language (English)", async ({ page }) => {
    await page.addInitScript(() => {
      window.localStorage.setItem("karry.language", "en");
    });

    await page.goto(portal("demo-carriere-b"));

    await expect(page.locator("html")).toHaveAttribute("lang", "en");
    await expect(page.getByRole("heading", { name: "Sign in to Karry" })).toBeVisible();
    await expect(page.getByTestId("tenant-name")).toHaveText("Carrière Démo B");
    await signIn(page, "commercial@demo-carriere-b.karry.test");
    await expect(page.getByText("Signed in as Demo Commercial")).toBeVisible();
  });

  test("TC-P1-IAM-002: a privileged user must set up two-step sign-in and never reaches the portal before", async ({ page }) => {
    await page.goto(portal("demo-carriere-a"));

    await signIn(page, "finance@demo-carriere-a.karry.test");

    await expect(page.getByRole("heading", { name: "Activez la double authentification" })).toBeVisible();
    await expect(page).toHaveURL(/\/realms\/karry\/login-actions\/required-action\?execution=CONFIGURE_TOTP/);
    expect(new URL(page.url()).host).not.toBe(new URL(portal("demo-carriere-a")).host);
  });

  test("the field app signs in through its own client with the tenant's branding", async ({ page }) => {
    await page.goto(field("demo-carriere-b"));

    await expect(page).toHaveURL(/client_id=karry-field/);
    await expect(page.getByTestId("tenant-name")).toHaveText("Carrière Démo B");
    await signIn(page, "site-manager@demo-carriere-b.karry.test");

    await expect(page.getByText("Connecté : Demo SiteManager")).toBeVisible();
    await expect(page.getByRole("button", { name: "Se déconnecter" })).toBeVisible();
    expect((await webStorage(page)).filter((entry) => entry.includes("eyJ"))).toEqual([]);
  });

  test("wrong passwords never reveal whether the account exists, then the account locks (US-P1-IAM-01 AC4)", async ({ page }) => {
    await page.goto(portal("demo-carriere-b"));

    for (let attempt = 0; attempt < 2; attempt++) {
      await signIn(page, "maintenance-planner@demo-carriere-b.karry.test", "wrong-password-123");
      await expect(page.getByText(/mot de passe invalide/i)).toBeVisible();
    }

    await signIn(page, "maintenance-planner@demo-carriere-b.karry.test");
    await expect(page.getByText(/mot de passe invalide/i), "locked: the right password is refused with the same message").toBeVisible();
  });
});

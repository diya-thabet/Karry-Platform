import { defineConfig, devices } from "@playwright/test";

// The apps (vite preview), Karry.Host and the local stack must be running: tools/e2e/run.sh starts them.
export default defineConfig({
  testDir: "./tests",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  reporter: process.env.CI ? [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]] : "list",
  // A French-speaking user (the default market); the English test chooses English in the app.
  use: { ...devices["Desktop Chrome"], locale: "fr-FR", trace: "retain-on-failure", screenshot: "only-on-failure" },
});

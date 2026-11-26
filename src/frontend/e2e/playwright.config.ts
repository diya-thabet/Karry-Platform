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
  // Long runs (TC-P2-SYNC-006, 72 h offline) are tagged @nightly: the nightly job runs only them (E2E_NIGHTLY=1).
  ...(process.env.E2E_NIGHTLY === "1" ? { grep: /@nightly/ } : { grepInvert: /@nightly/ }),
  reporter: process.env.CI ? [["list"], ["html", { open: "never", outputFolder: "playwright-report" }]] : "list",
  // A French-speaking user (the default market); the English test chooses English in the app.
  // Chromium's fake camera (a moving test pattern) stands in for the tablet's back camera; no prompt asks for it.
  use: {
    ...devices["Desktop Chrome"], locale: "fr-FR", trace: "retain-on-failure", screenshot: "only-on-failure",
    launchOptions: { args: ["--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream"] },
  },
});

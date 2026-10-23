import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { playwright } from "@vitest/browser-playwright";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [react(), tailwindcss()],
  test: {
    globals: true,
    projects: [
      {
        extends: true,
        test: { name: "unit", environment: "jsdom", include: ["src/**/*.test.{ts,tsx}"], setupFiles: ["./src/test-setup.ts"] },
      },
      {
        // TC-P1-OFF-006: every story in every theme, in a real Chromium (axe needs layout for colour contrast).
        extends: true,
        test: {
          name: "a11y",
          include: ["src/**/*.a11y.{ts,tsx}"],
          browser: { enabled: true, provider: playwright(), headless: true, instances: [{ browser: "chromium" }] },
        },
      },
    ],
  },
});

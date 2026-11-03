import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { VitePWA } from "vite-plugin-pwa";
import { loadEnv } from "vite";
import { defineConfig } from "vitest/config";
import { securityHeaders } from "../../security-headers";

// Local addresses: http://localhost:5174 and the demo tenants' http://<slug>.localhost:5174 (browsers resolve
// *.localhost to this machine; the first label names the company at enrollment). /api goes to Karry.Host, so the app
// and the API share an origin as behind the production ingress.
const server = {
  port: 5174,
  strictPort: true,
  allowedHosts: [".localhost"],
  proxy: { "/api": { target: process.env.KARRY_API_URL ?? "http://localhost:5000", changeOrigin: false } },
};

export default defineConfig(({ mode }) => ({
  plugins: [
    react(),
    tailwindcss(),
    // Installable and offline after the first load (US-P1-OFF-01, ADR-0028 §7): the whole app is precached; a new
    // version waits until the person accepts it on the home screen ("prompt"), never in the middle of a form.
    VitePWA({
      registerType: "prompt",
      injectRegister: false,
      manifest: {
        name: "Karry Terrain",
        short_name: "Karry",
        description: "Saisies de poste hors ligne pour les opérateurs, pontiers et magasiniers.",
        lang: "fr",
        start_url: "/",
        scope: "/",
        display: "standalone",
        orientation: "any",
        background_color: "#ffffff",
        theme_color: "#0b1f33",
        icons: [
          { src: "icon.svg", sizes: "any", type: "image/svg+xml", purpose: "any" },
          { src: "icon-maskable.svg", sizes: "any", type: "image/svg+xml", purpose: "maskable" },
        ],
      },
      includeAssets: ["icon.svg", "icon-maskable.svg"],
      workbox: {
        globPatterns: ["**/*.{js,css,html,svg,woff2,webmanifest}"],
        navigateFallback: "/index.html",
        // The API is never cached by the service worker: reference data lives in IndexedDB (ADR-0028 §5).
        navigateFallbackDenylist: [/^\/api\//],
      },
    }),
  ],
  server,
  // The production page policy (H-W2) on the production build: the browser E2E tests run against it.
  preview: { ...server, headers: securityHeaders("field", loadEnv(mode, process.cwd(), "VITE_")) },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
  },
}));

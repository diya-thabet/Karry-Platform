import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// Local addresses (US-P1-IAM-01): Keycloak's portal client allows http://localhost:5173 and the demo tenants'
// http://<slug>.localhost:5173 (browsers resolve *.localhost to this machine). /api goes to Karry.Host, so the app
// and the API share an origin as behind the production ingress.
const server = {
  port: 5173,
  strictPort: true,
  allowedHosts: [".localhost"],
  proxy: { "/api": { target: process.env.KARRY_API_URL ?? "http://localhost:5000", changeOrigin: false } },
};

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server,
  preview: server,
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
  },
});

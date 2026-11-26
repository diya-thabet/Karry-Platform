import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { loadEnv, type Plugin } from "vite";
import { defineConfig } from "vitest/config";
import { isVerifyRequest, securityHeaders, type PolicyEnvironment } from "../../security-headers";

// Local addresses (US-P1-IAM-01): Keycloak's portal client allows http://localhost:5173 and the demo tenants'
// http://<slug>.localhost:5173 (browsers resolve *.localhost to this machine). /api goes to Karry.Host, so the app
// and the API share an origin as behind the production ingress.
const server = {
  port: 5173,
  strictPort: true,
  allowedHosts: [".localhost"],
  proxy: { "/api": { target: process.env.KARRY_API_URL ?? "http://localhost:5000", changeOrigin: false } },
};

/**
 * The public verification page gets its own, stricter headers (ADR-0040 §5): Vite sets the portal's on every answer,
 * so on a `/verify/` request each header Vite or the static server sets is replaced by the page's own value.
 */
function verifyPageHeaders(environment: PolicyEnvironment): Plugin {
  const strict = new Map(Object.entries(securityHeaders("verify", environment)).map(([name, value]) => [name.toLowerCase(), value]));
  return {
    name: "karry-verify-page-headers",
    configurePreviewServer(preview) {
      preview.middlewares.use((request, response, next) => {
        if (isVerifyRequest(request.url)) {
          const setHeader = response.setHeader.bind(response);
          response.setHeader = (name, value) => setHeader(name, strict.get(name.toLowerCase()) ?? value);
          for (const [name, value] of strict) {
            setHeader(name, value);
          }
        }

        next();
      });
    },
  };
}

export default defineConfig(({ mode }) => ({
  plugins: [react(), tailwindcss(), verifyPageHeaders(loadEnv(mode, process.cwd(), "VITE_"))],
  server,
  // The production page policy (H-W2) on the production build: the browser E2E tests run against it.
  preview: { ...server, headers: securityHeaders("portal", loadEnv(mode, process.cwd(), "VITE_")) },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
    // Above the 5 s a findBy*/waitFor may wait (test-setup.ts), so a slow page fails on its own message.
    testTimeout: 15_000,
  },
}));

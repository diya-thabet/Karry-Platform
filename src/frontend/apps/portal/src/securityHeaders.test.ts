import { contentSecurityPolicy, originOf, securityHeaders } from "../../../security-headers";

// Hardening plan H-W2: the page policy of both apps (src/frontend/security-headers.ts). The browser E2E run proves
// the real pages work under it; these pin what it allows.

function directives(policy: string): Map<string, string[]> {
  return new Map(policy.split("; ").map((part) => {
    const [name, ...values] = part.split(" ");
    return [name ?? "", values];
  }));
}

describe("page security policy", () => {
  const production = {
    VITE_AUTH_AUTHORITY: "https://auth.karry.app/realms/karry",
    VITE_MAP_STYLE_URL: "https://tiles.example.org/style.json",
    VITE_ERROR_TRACKING_DSN: "https://public-key@o1.ingest.example.io/42",
  };

  it("never allows inline or remote scripts, plugins, a foreign base or being framed", () => {
    for (const app of ["portal", "field"] as const) {
      const policy = directives(contentSecurityPolicy(app, production));
      expect(policy.get("script-src")).toEqual(["'self'"]);
      expect(policy.get("object-src")).toEqual(["'none'"]);
      expect(policy.get("base-uri")).toEqual(["'self'"]);
      expect(policy.get("frame-ancestors")).toEqual(["'none'"]);
      // Styles only (react-style-singleton and React insert <style> elements); never for scripts.
      expect(policy.get("style-src")).toEqual(["'self'", "'unsafe-inline'"]);
      expect(contentSecurityPolicy(app, production)).not.toContain("unsafe-eval");
    }
  });

  it("lets the portal reach only Keycloak, the map and error tracking besides itself", () => {
    const policy = directives(contentSecurityPolicy("portal", production));
    expect(policy.get("connect-src")).toEqual(["'self'", "https://auth.karry.app", "https://tiles.example.org", "https://o1.ingest.example.io"]);
    expect(policy.get("frame-src")).toEqual(["https://auth.karry.app"]);
    expect(policy.get("worker-src")).toEqual(["'self'", "blob:"]);
  });

  it("keeps the field app to its own origin: no Keycloak, no frames", () => {
    const policy = directives(contentSecurityPolicy("field", production));
    expect(policy.get("connect-src")).toEqual(["'self'", "https://o1.ingest.example.io"]);
    expect(policy.get("frame-src")).toEqual(["'none'"]);
  });

  it("adds nothing for settings that are missing or not addresses", () => {
    expect(originOf(undefined)).toBeUndefined();
    expect(originOf("not an address")).toBeUndefined();
    expect(directives(contentSecurityPolicy("field", {})).get("connect-src")).toEqual(["'self'"]);
    expect(directives(contentSecurityPolicy("portal", {})).get("connect-src")).toEqual(["'self'", "http://localhost:8080", "https://demotiles.maplibre.org"]);
  });

  it("only the field app may use the camera", () => {
    expect(securityHeaders("field", {})["Permissions-Policy"]).toContain("camera=(self)");
    expect(securityHeaders("portal", {})["Permissions-Policy"]).toContain("camera=()");
    expect(securityHeaders("portal", {})["X-Frame-Options"]).toBe("DENY");
  });
});

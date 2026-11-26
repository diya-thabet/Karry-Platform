import { contentSecurityPolicy, isVerifyRequest, originOf, securityHeaders } from "../../../security-headers";

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
    VITE_EVIDENCE_ORIGIN: "https://evidence.karry.app",
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

  it("keeps the field app to its own origin and the evidence store (its uploads): no Keycloak, no frames", () => {
    const policy = directives(contentSecurityPolicy("field", production));
    expect(policy.get("connect-src")).toEqual(["'self'", "https://evidence.karry.app", "https://o1.ingest.example.io"]);
    expect(policy.get("frame-src")).toEqual(["'none'"]);
  });

  it("adds nothing for settings that are missing or not addresses", () => {
    expect(originOf(undefined)).toBeUndefined();
    expect(originOf("not an address")).toBeUndefined();
    expect(directives(contentSecurityPolicy("field", {})).get("connect-src")).toEqual(["'self'", "http://127.0.0.1:8333"]);
    expect(directives(contentSecurityPolicy("field", { VITE_EVIDENCE_ORIGIN: "not an address" })).get("connect-src")).toEqual(["'self'"]);
    expect(directives(contentSecurityPolicy("portal", {})).get("connect-src")).toEqual(["'self'", "http://localhost:8080", "https://demotiles.maplibre.org"]);
  });

  it("only the field app may use the camera and the position", () => {
    expect(securityHeaders("field", {})["Permissions-Policy"]).toContain("camera=(self)");
    expect(securityHeaders("field", {})["Permissions-Policy"]).toContain("geolocation=(self)");
    expect(securityHeaders("portal", {})["Permissions-Policy"]).toContain("camera=()");
    expect(securityHeaders("portal", {})["Permissions-Policy"]).toContain("geolocation=()");
    expect(securityHeaders("portal", {})["X-Frame-Options"]).toBe("DENY");
  });

  it("ADR-0040 §5: the public verification page reaches only its own API (and error tracking), with no frame, worker, form or referrer", () => {
    const policy = directives(contentSecurityPolicy("verify", production));
    expect(policy.get("script-src")).toEqual(["'self'"]);
    expect(policy.get("connect-src")).toEqual(["'self'", "https://o1.ingest.example.io"]);
    expect(policy.get("frame-src")).toEqual(["'none'"]);
    expect(policy.get("worker-src")).toEqual(["'none'"]);
    expect(policy.get("form-action")).toEqual(["'none'"]);
    expect(policy.get("frame-ancestors")).toEqual(["'none'"]);
    expect(contentSecurityPolicy("verify", production)).not.toContain("auth.karry.app");
    expect(securityHeaders("verify", production)["Referrer-Policy"]).toBe("no-referrer");
  });

  it("gives the verification policy to /verify/{id} only", () => {
    expect(isVerifyRequest("/verify/0192f0c1-0000-7000-8000-00000000d001")).toBe(true);
    expect(isVerifyRequest("/verify/0192f0c1-0000-7000-8000-00000000d001?x=1")).toBe(true);
    expect(isVerifyRequest("/verify/")).toBe(false);
    expect(isVerifyRequest("/verify/a/b")).toBe(false);
    expect(isVerifyRequest("/review/board")).toBe(false);
    expect(isVerifyRequest("/assets/index.js")).toBe(false);
    expect(isVerifyRequest(undefined)).toBe(false);
  });
});

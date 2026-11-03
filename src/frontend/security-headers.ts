// The page policy of the portal and field app (hardening plan H-W2). Served by `vite preview` (the production build,
// which the browser E2E tests run against, so a violation fails CI) and copied by the production web server (P1.17).
// Not on the dev server: its hot reload injects inline scripts, which this policy forbids on purpose.
// The API sends its own, stricter headers (Karry.BuildingBlocks/Api/SecurityHeaders.cs).

export type App = "portal" | "field";

/** The settings the policy depends on: the same VITE_ variables the apps read at build time. */
export interface PolicyEnvironment {
  VITE_AUTH_AUTHORITY?: string | undefined;
  VITE_MAP_STYLE_URL?: string | undefined;
  VITE_ERROR_TRACKING_DSN?: string | undefined;
}

const defaultAuthority = "http://localhost:8080/realms/karry";
const defaultMapStyle = "https://demotiles.maplibre.org/style.json";

/** The origin of an address, or nothing when it is missing or not an address (a DSN carries a key before the host). */
export function originOf(address: string | undefined): string | undefined {
  if (!address) {
    return undefined;
  }
  try {
    return new URL(address).origin;
  } catch {
    return undefined;
  }
}

function sources(...values: (string | undefined)[]): string {
  return [...new Set(values.filter((value): value is string => Boolean(value)))].join(" ");
}

/** Content-Security-Policy of an app: only its own origin, plus the services it really talks to. */
export function contentSecurityPolicy(app: App, environment: PolicyEnvironment): string {
  const errors = originOf(environment.VITE_ERROR_TRACKING_DSN);
  const directives: Record<string, string> =
    app === "portal"
      ? (() => {
          const auth = originOf(environment.VITE_AUTH_AUTHORITY ?? defaultAuthority);
          const map = originOf(environment.VITE_MAP_STYLE_URL ?? defaultMapStyle);
          return {
            "img-src": sources("'self'", "data:", "blob:", map),
            "connect-src": sources("'self'", auth, map, errors),
            // MapLibre draws in web workers made from blob: URLs.
            "worker-src": sources("'self'", "blob:"),
            // oidc-client-ts renews silently with the refresh token; its fallback is a hidden Keycloak frame.
            "frame-src": sources(auth),
            "form-action": sources("'self'", auth),
          };
        })()
      : {
          "img-src": sources("'self'", "data:", "blob:"),
          "connect-src": sources("'self'", errors),
          // The service worker that keeps the app usable offline (ADR-0028 §7).
          "worker-src": "'self'",
          "frame-src": "'none'",
          "form-action": "'self'",
        };
  return Object.entries({
    "default-src": "'self'",
    "script-src": "'self'",
    // 'unsafe-inline' for styles only: the dialogs' scroll lock (react-style-singleton, under Radix) and React itself
    // insert <style> elements at run time, which a static site cannot cover with a nonce or a hash. Measured, not
    // assumed: with "style-src 'self'" the E2E guard caught it on every page. Inline styles cannot run code; scripts
    // stay 'self' only. Accepted ZAP finding 10055 (tests/security/zap-accepted.tsv).
    "style-src": "'self' 'unsafe-inline'",
    "font-src": "'self' data:",
    "manifest-src": "'self'",
    "object-src": "'none'",
    "base-uri": "'self'",
    "frame-ancestors": "'none'",
    ...directives,
  })
    .map(([directive, value]) => `${directive} ${value}`)
    .join("; ");
}

/** Every header the app's pages are served with. */
export function securityHeaders(app: App, environment: PolicyEnvironment): Record<string, string> {
  return {
    "Content-Security-Policy": contentSecurityPolicy(app, environment),
    "X-Content-Type-Options": "nosniff",
    "X-Frame-Options": "DENY",
    "Referrer-Policy": "same-origin",
    "Cross-Origin-Opener-Policy": "same-origin",
    // The field app photographs meters (camera); nothing else is ever asked of the device.
    "Permissions-Policy":
      app === "field"
        ? "camera=(self), microphone=(), geolocation=(), payment=(), usb=()"
        : "camera=(), microphone=(), geolocation=(), payment=(), usb=()",
  };
}

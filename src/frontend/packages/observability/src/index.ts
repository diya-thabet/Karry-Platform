import * as Sentry from "@sentry/react";

export interface ErrorTrackingOptions {
  /** Sentry-compatible DSN; empty or missing: tracking is off (default until the provider is chosen, P1.17). */
  dsn?: string | undefined;
  /** Build version (git SHA or tag), so an error points at the exact release. */
  release: string;
  environment: string;
  app: "portal" | "field";
}

/**
 * What the SDK may collect: nothing personal. Sentry 11 collects user info, cookies, headers, bodies and query strings
 * **by default**; every category is switched off here, and {@link scrub} removes them again before sending.
 */
export const dataCollection = {
  userInfo: false,
  cookies: false,
  httpHeaders: false,
  httpBodies: [],
  urlQueryParams: false,
} as const satisfies NonNullable<Sentry.BrowserOptions["dataCollection"]>;

/**
 * Starts frontend error tracking (US-P1-OPS-02 AC2), or does nothing without a DSN. No personal data leaves the
 * device: no user identity, IP, cookies, headers, bodies or query strings, and URLs reduced to their path; the tenant
 * is attached only as a hash (see {@link setTenant}).
 */
export function initErrorTracking(options: ErrorTrackingOptions): boolean {
  if (!options.dsn) {
    return false;
  }

  Sentry.init({
    dsn: options.dsn,
    release: options.release,
    environment: options.environment,
    dataCollection: { ...dataCollection, httpBodies: [] },
    initialScope: { tags: { app: options.app } },
    beforeSend: scrub,
    beforeBreadcrumb: (breadcrumb) => (breadcrumb.category === "console" || breadcrumb.category === "ui.input" ? null : breadcrumb),
  });
  return true;
}

/** Attaches the tenant as SHA-256 (first 16 hex): errors can be grouped per tenant without revealing which one. */
export async function setTenant(tenantId: string): Promise<void> {
  Sentry.setTag("tenant", await hashTenant(tenantId));
}

export async function hashTenant(tenantId: string): Promise<string> {
  const digest = await globalThis.crypto.subtle.digest("SHA-256", new TextEncoder().encode(`karry-tenant:${tenantId.toLowerCase()}`));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("").slice(0, 16);
}

/** Last line of defence before an event leaves the device. */
export function scrub<T extends Sentry.ErrorEvent>(event: T): T {
  delete event.user;
  if (event.request) {
    delete event.request.cookies;
    delete event.request.headers;
    delete event.request.data;
    delete event.request.query_string;
    if (event.request.url) {
      event.request.url = pathOnly(event.request.url);
    }
  }

  for (const breadcrumb of event.breadcrumbs ?? []) {
    if (typeof breadcrumb.data?.url === "string") {
      breadcrumb.data.url = pathOnly(breadcrumb.data.url);
    }
  }

  return event;
}

function pathOnly(url: string): string {
  try {
    return new URL(url, "https://karry.invalid").pathname;
  } catch {
    return "[url]";
  }
}

export { ErrorBoundary as ErrorTrackingBoundary } from "@sentry/react";

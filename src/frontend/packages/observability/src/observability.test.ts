import type { ErrorEvent } from "@sentry/react";
import { dataCollection, hashTenant, initErrorTracking, scrub } from "./index";

describe("frontend error tracking (US-P1-OPS-02 AC2)", () => {
  it("is off without a DSN", () => {
    expect(initErrorTracking({ dsn: "", release: "abc123", environment: "test", app: "portal" })).toBe(false);
    expect(initErrorTracking({ release: "abc123", environment: "test", app: "field" })).toBe(false);
  });

  it("tells the SDK to collect no personal data (Sentry 11 collects it by default)", () => {
    expect(dataCollection).toEqual({ userInfo: false, cookies: false, httpHeaders: false, httpBodies: [], urlQueryParams: false });
  });

  it("hashes the tenant: stable, short, and not the identifier", async () => {
    const tenant = "01970000-0000-7000-8000-00000000000A";
    const hash = await hashTenant(tenant);

    expect(hash).toMatch(/^[0-9a-f]{16}$/);
    expect(await hashTenant(tenant.toLowerCase())).toBe(hash);
    expect(await hashTenant("01970000-0000-7000-8000-00000000000b")).not.toBe(hash);
    expect(hash).not.toContain("0197");
  });

  it("removes identity, cookies, headers, bodies and query strings before sending", () => {
    const raw: ErrorEvent = {
      type: undefined,
      user: { email: "jane.doe@example.com", ip_address: "196.203.10.7" },
      request: {
        url: "https://acme.karry.app/assets/42?email=jane.doe@example.com",
        cookies: { session: "secret" },
        headers: { Authorization: "Bearer token" },
        data: { name: "Jane" },
        query_string: "email=jane.doe@example.com",
      },
      breadcrumbs: [{ data: { url: "https://acme.karry.app/api/v1/x?token=abc" } }],
    };
    const event = scrub(raw);

    const sent = JSON.stringify(event);
    for (const secret of ["jane.doe", "196.203", "secret", "Bearer", "Jane", "token=abc"]) {
      expect(sent).not.toContain(secret);
    }
    expect(event.request?.url).toBe("/assets/42");
    expect(event.breadcrumbs?.[0]?.data?.url).toBe("/api/v1/x");
  });
});

import { expect, test } from "./guard";

// Hardening plan H-W2 on the running apps (vite preview, the production build) and on the real API behind them.
const apps = [
  { name: "portal", origin: "http://localhost:5173", connects: "http://localhost:8080" },
  { name: "field", origin: "http://localhost:5174", connects: undefined },
];

for (const app of apps) {
  test(`the ${app.name} pages are served with the production page policy`, async ({ request }) => {
    const response = await request.get(`${app.origin}/`);
    const headers = response.headers();

    expect(response.status()).toBe(200);
    expect(headers["content-security-policy"]).toContain("script-src 'self';");
    expect(headers["content-security-policy"]).toContain("frame-ancestors 'none'");
    if (app.connects) {
      expect(headers["content-security-policy"]).toContain(app.connects);
    }
    expect(headers["x-content-type-options"]).toBe("nosniff");
    expect(headers["x-frame-options"]).toBe("DENY");
    expect(headers["referrer-policy"]).toBe("same-origin");
  });
}

test("the API's answers carry its own stricter headers, errors included", async ({ request }) => {
  for (const path of ["/api/v1/tenancy/public/demo-carriere-a", "/api/v1/identity/users", "/api/v1/no-such-thing"]) {
    const headers = (await request.get(`http://localhost:5173${path}`)).headers();
    expect(headers["content-security-policy"], path).toBe("default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'");
    expect(headers["x-content-type-options"], path).toBe("nosniff");
    expect(headers["cache-control"], path).toBeTruthy();
    expect(headers.server, path).toBeUndefined();
  }
});

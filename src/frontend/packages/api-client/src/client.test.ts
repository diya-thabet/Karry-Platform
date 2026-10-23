import { ApiError } from "./errors";
import { createApiClient } from "./client";

interface Call { url: string; method: string; headers: Headers; body: string | null }

function fakeApi(response: () => Response) {
  const calls: Call[] = [];
  const fetch = async (input: Request | string | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(input, init);
    calls.push({ url: request.url, method: request.method, headers: request.headers, body: request.body ? await request.text() : null });
    return response();
  };
  return { calls, fetch: fetch };
}

const json = (body: unknown, status = 200, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json", ...headers } });

describe("typed API client", () => {
  it("calls a typed endpoint with the access token and returns typed data", async () => {
    const api = fakeApi(() => json({ id: "0197…", slug: "acme", name: "Acme", createdAt: "2026-10-23T08:00:00Z" }));
    const client = createApiClient({ baseUrl: "https://api.karry.test", getAccessToken: () => "token-1", fetch: api.fetch });

    const { data } = await client.GET("/api/v1/tenancy/current");

    expect(data?.slug).toBe("acme");
    expect(api.calls[0]?.url).toBe("https://api.karry.test/api/v1/tenancy/current");
    expect(api.calls[0]?.headers.get("Authorization")).toBe("Bearer token-1");
  });

  it("sends no Authorization header when there is no token", async () => {
    const api = fakeApi(() => json({ slug: "acme", name: "Acme" }));
    const client = createApiClient({ baseUrl: "http://localhost", fetch: api.fetch });

    await client.GET("/api/v1/tenancy/public/{slug}", { params: { path: { slug: "acme" } } });

    expect(api.calls[0]?.headers.has("Authorization")).toBe(false);
    expect(api.calls[0]?.url).toBe("http://localhost/api/v1/tenancy/public/acme");
  });

  it("adds an Idempotency-Key to writes, a new one per call, and keeps a caller's key", async () => {
    const api = fakeApi(() => json({ id: "x", slug: "acme", name: "Acme", createdAt: "2026-10-23T08:00:00Z" }, 201));
    const client = createApiClient({ baseUrl: "http://localhost", fetch: api.fetch });

    await client.POST("/api/v1/tenancy/tenants", { body: { slug: "acme", name: "Acme" } });
    await client.POST("/api/v1/tenancy/tenants", { body: { slug: "acme", name: "Acme" } });
    await client.POST("/api/v1/tenancy/tenants", { body: { slug: "acme", name: "Acme" }, headers: { "Idempotency-Key": "retry-1" } });
    await client.GET("/api/v1/tenancy/current");

    const keys = api.calls.map((call) => call.headers.get("Idempotency-Key"));
    expect(keys[0]).toMatch(/^[0-9a-f-]{36}$/);
    expect(keys[1]).not.toBe(keys[0]);
    expect(keys[2]).toBe("retry-1");
    expect(keys[3]).toBeNull();
    expect(JSON.parse(api.calls[0]?.body ?? "{}")).toEqual({ slug: "acme", name: "Acme" });
  });

  it("throws an ApiError with the problem code and the correlation ID for any failure", async () => {
    const api = fakeApi(() => json({ code: "tenant.missing", title: "No tenant" }, 403, { "X-Correlation-Id": "4bf92f3577b34da6a3ce929d0e0e4736" }));
    const client = createApiClient({ baseUrl: "http://localhost", fetch: api.fetch });

    const failure = await client.GET("/api/v1/tenancy/current").catch((error: unknown) => error);

    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 403, code: "tenant.missing", correlationId: "4bf92f3577b34da6a3ce929d0e0e4736" });
  });
});

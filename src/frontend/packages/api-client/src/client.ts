import createClient, { type Middleware } from "openapi-fetch";
import { ApiError } from "./errors";
import type { paths } from "./schema";

export interface ApiClientOptions {
  /** Origin of the API ("" for same origin, e.g. https://api.karry.app). */
  baseUrl: string;
  /** Current access token (Keycloak, P1.8); omitted: anonymous requests. */
  getAccessToken?: () => string | undefined | Promise<string | undefined>;
  /** Injected in tests. */
  fetch?: typeof globalThis.fetch;
}

const writes = new Set(["POST", "PUT", "PATCH", "DELETE"]);

/**
 * Typed client of the Karry API, generated from the committed OpenAPI contract (`pnpm --filter @karry/api-client
 * generate`): paths, parameters and bodies are checked at compile time. Every non-2xx response is thrown as an
 * {@link ApiError} carrying the problem code and the correlation ID, so the UI has one way to show failures.
 * Writes get an `Idempotency-Key` (safe retries, api-standards) unless the caller sets one.
 */
export function createApiClient(options: ApiClientOptions) {
  const client = createClient<paths>({ baseUrl: options.baseUrl, ...(options.fetch ? { fetch: options.fetch } : {}) });

  const middleware: Middleware = {
    async onRequest({ request }) {
      const token = await options.getAccessToken?.();
      if (token) {
        request.headers.set("Authorization", `Bearer ${token}`);
      }

      if (writes.has(request.method) && !request.headers.has("Idempotency-Key")) {
        request.headers.set("Idempotency-Key", globalThis.crypto.randomUUID());
      }

      return request;
    },
    async onResponse({ response }) {
      if (!response.ok) {
        throw await ApiError.fromResponse(response.clone());
      }

      return response;
    },
  };
  client.use(middleware);
  return client;
}

export type ApiClient = ReturnType<typeof createApiClient>;

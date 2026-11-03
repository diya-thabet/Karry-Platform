import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";
import { ApiError } from "@karry/api-client";

/**
 * Every failed query or mutation reaches the error dialog (with its support reference), with a way to load the
 * screen's data again once the person fixed the cause (signing in again with a second factor). A failed query is tried
 * again once, except after 429 `request.rate_limited`: the API asked to wait, and an immediate retry only adds load.
 */
export function createQueryClient(report: (error: unknown, reload?: () => void) => void, retry: number | false = 1): QueryClient {
  const reload = () => { void client.invalidateQueries(); };
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: (error) => { report(error, reload); } }),
    mutationCache: new MutationCache({ onError: (error) => { report(error, reload); } }),
    defaultOptions: { queries: { retry: (failures, error) => retry !== false && failures < retry && !isRateLimited(error), staleTime: 30_000 } },
  });
  return client;
}

function isRateLimited(error: unknown): boolean {
  return error instanceof ApiError && error.status === 429;
}

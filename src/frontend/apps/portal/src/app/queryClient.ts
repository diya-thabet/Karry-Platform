import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";

/**
 * Every failed query or mutation reaches the error dialog (with its support reference), with a way to load the
 * screen's data again once the person fixed the cause (signing in again with a second factor).
 */
export function createQueryClient(report: (error: unknown, reload?: () => void) => void, retry: number | false = 1): QueryClient {
  const reload = () => { void client.invalidateQueries(); };
  const client: QueryClient = new QueryClient({
    queryCache: new QueryCache({ onError: (error) => { report(error, reload); } }),
    mutationCache: new MutationCache({ onError: (error) => { report(error, reload); } }),
    defaultOptions: { queries: { retry, staleTime: 30_000 } },
  });
  return client;
}

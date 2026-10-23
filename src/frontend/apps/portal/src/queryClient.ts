import { MutationCache, QueryCache, QueryClient } from "@tanstack/react-query";

/** Every failed query or mutation reaches the error dialog (with its support reference). */
export function createQueryClient(report: (error: unknown) => void, retry: number | false = 1): QueryClient {
  return new QueryClient({
    queryCache: new QueryCache({ onError: report }),
    mutationCache: new MutationCache({ onError: report }),
    defaultOptions: { queries: { retry, staleTime: 30_000 } },
  });
}

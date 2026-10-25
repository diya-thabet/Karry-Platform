import { useQuery } from "@tanstack/react-query";
import { createContext, use, useCallback } from "react";
import { ApiError, type ApiClient } from "@karry/api-client";
import { useAuth } from "@karry/auth";

export const ApiContext = createContext<ApiClient | null>(null);

/** The typed API client (token and Idempotency-Key added by @karry/api-client). */
export function useApi(): ApiClient {
  const client = use(ApiContext);
  if (!client) {
    throw new Error("useApi must be used inside <ApiProvider>.");
  }

  return client;
}

export const StepUpCode = "auth.step_up_required";

/**
 * Runs a change the API may refuse until the person signs in again (privileged changes, US-P1-IAM-05 AC3): on
 * `auth.step_up_required` it opens the Keycloak pop-up and, once they confirmed, sends the same change once more.
 * The page and the form stay as they are; a closed pop-up surfaces as the original refusal.
 */
export function useWithStepUp(): <T>(call: () => Promise<T>) => Promise<T> {
  const { stepUp } = useAuth();
  return useCallback(
    async <T,>(call: () => Promise<T>): Promise<T> => {
      try {
        return await call();
      } catch (error) {
        if (error instanceof ApiError && error.code === StepUpCode && (await stepUp())) {
          return call();
        }

        throw error;
      }
    },
    [stepUp],
  );
}

/**
 * What the signed-in person may do, for showing only usable screens and buttons. Display only: the API checks every
 * call (ADR-0022). Until it is known, nothing is shown.
 */
export function useAccess(): { loaded: boolean; can: (permission: string) => boolean } {
  const api = useApi();
  const query = useQuery({
    queryKey: ["identity", "me", "access"],
    queryFn: async () => (await api.GET("/api/v1/identity/me/access")).data,
  });
  const permissions = new Set(query.data?.permissions ?? []);
  return { loaded: query.isSuccess, can: (permission) => permissions.has(permission) };
}

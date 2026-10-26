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

/**
 * What the company's plan includes (US-P1-BIL-03 AC2), for showing or locking features. Display only: the API refuses
 * a feature that is not in the plan (`billing.feature_not_in_plan`). Until it is known, nothing is shown.
 */
export function useEntitlements(): { loaded: boolean; includes: (feature: string) => boolean; planCode?: string | undefined; status?: string | undefined } {
  const api = useApi();
  const query = useQuery({
    queryKey: ["billing", "entitlements"],
    queryFn: async () => (await api.GET("/api/v1/billing/entitlements")).data,
    staleTime: 60_000,
  });
  const features = new Set(query.data?.features ?? []);
  return { loaded: query.isSuccess, includes: (feature) => features.has(feature), planCode: query.data?.planCode, status: query.data?.status };
}

/** One feature: whether the plan includes it, once known (US-P1-BIL-03 AC2). */
export function useEntitlement(feature: string): { loaded: boolean; included: boolean } {
  const { loaded, includes } = useEntitlements();
  return { loaded, included: includes(feature) };
}

/** One metric now; limit and ceiling are null when unlimited. State: within, over or blocked. */
export interface UsageLine {
  metric: string;
  usage: number;
  limit: number | null;
  ceiling: number | null;
  state: string;
}

export const BillingReadPermission = "billing.subscription.read";

/**
 * The company's usage against its plan (US-P1-BIL-04), for billing readers only (others never ask). Display only:
 * the API refuses creation beyond the grace (`billing.limit_reached`).
 */
export function useUsage(): { loaded: boolean; items: UsageLine[]; snapshotOn: string | null | undefined } {
  const api = useApi();
  const { can } = useAccess();
  const query = useQuery({
    queryKey: ["billing", "usage"],
    queryFn: async () => (await api.GET("/api/v1/billing/usage")).data,
    enabled: can(BillingReadPermission),
    staleTime: 60_000,
  });
  // 64-bit counts may arrive as JSON strings (OpenAPI int64); they are small enough for a number.
  const items = (query.data?.items ?? []).map((line) => ({
    metric: line.metric,
    usage: Number(line.usage),
    limit: line.limit == null ? null : Number(line.limit),
    ceiling: line.ceiling == null ? null : Number(line.ceiling),
    state: line.state,
  }));
  return { loaded: query.isSuccess, items, snapshotOn: query.data?.snapshotOn };
}

/** Subscription states in which only billing, export and the person's own account remain (ADR-0023). */
export const endedStates = new Set(["cancelled", "expired", "deleted"]);

export const SettingsManagePermission = "tenancy.settings.manage";

export const currentTenantKey = ["tenancy", "current"] as const;

/**
 * The signed-in person's company and its settings (US-P1-TEN-04). Saving settings refreshes this query, so a change
 * shows at once without signing in again.
 */
export function useCurrentTenant() {
  const api = useApi();
  const query = useQuery({
    queryKey: currentTenantKey,
    queryFn: async () => (await api.GET("/api/v1/tenancy/current")).data ?? null,
  });
  return { loaded: query.isSuccess, tenant: query.data ?? null };
}


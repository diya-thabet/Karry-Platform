import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import type { AuthUser } from "@karry/auth";
import type { Status } from "@karry/ui";
import { useApi } from "../api";

export type OperatorTenant = components["schemas"]["OperatorTenantResponse"];
export type MrrTotal = components["schemas"]["MrrTotalResponse"];

export const OperatorRole = "operator";
export const operatorTenantsKey = ["operator", "tenants"] as const;

/**
 * A platform operator working on the platform address: no company in the token, so the portal shows the operator
 * console instead of a company's screens (ADR-0029 §5). Display only: the API checks the operator policy on every call.
 */
export function isOperator(user: AuthUser | null | undefined): boolean {
  return user?.platformRole === OperatorRole && user.tenantId === undefined;
}

/** How each subscription state looks in the list: in use, waiting, unpaid, or stopped. */
export const statusLook: Record<string, Status> = {
  trial: "pending", active: "ok", past_due: "warning", restricted: "warning", suspended: "error", cancelled: "error", expired: "error", deleted: "error",
};

/** A company address as the API accepts it (3–40 lowercase letters, digits or inner single hyphens). */
export function slugValid(slug: string): boolean {
  return slug.length >= 3 && slug.length <= 40 && /^[a-z0-9](?:[a-z0-9]|-(?!-))*[a-z0-9]$/.test(slug);
}

/** A suggested address from the company name: "Carrière de Sfax" → "carriere-de-sfax". */
export function slugFrom(name: string): string {
  return name.normalize("NFD").replace(/\p{M}/gu, "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40).replace(/-+$/, "");
}

/** Every company with its counts, plan, state, MRR and last activity (US-P1-OPS-03 AC1). */
export function useOperatorTenants() {
  const api = useApi();
  return useQuery({
    queryKey: operatorTenantsKey,
    queryFn: async () => (await api.GET("/api/v1/tenancy/tenants")).data ?? { tenants: [], mrr: [] },
  });
}

export type Subscription = components["schemas"]["SubscriptionResponse"];
export type Invoice = components["schemas"]["InvoiceResponse"];

export const subscriptionKey = (tenantId: string) => ["operator", "tenants", tenantId, "subscription"] as const;
export const invoicesKey = (tenantId: string) => ["operator", "tenants", tenantId, "invoices"] as const;

/** What an operator may do to a subscription in each state (the API refuses the rest: `billing.transition_invalid`). */
export const actionsFor: Record<string, readonly string[]> = {
  trial: ["activate"], expired: ["activate"], active: ["missed-payment", "cancel"], past_due: ["payment"], restricted: ["payment"],
  suspended: ["payment", "cancel"], cancelled: [], deleted: [],
};

/** A company's subscription, as operators manage it until the billing provider (US-P1-BIL-02). */
export function useTenantSubscription(tenantId: string) {
  const api = useApi();
  return useQuery({
    queryKey: subscriptionKey(tenantId),
    queryFn: async () => (await api.GET("/api/v1/billing/tenants/{tenantId}/subscription", { params: { path: { tenantId } } })).data ?? null,
  });
}

export function useTenantInvoices(tenantId: string) {
  const api = useApi();
  return useQuery({
    queryKey: invoicesKey(tenantId),
    queryFn: async () => (await api.GET("/api/v1/billing/tenants/{tenantId}/invoices", { params: { path: { tenantId } } })).data?.items ?? [],
  });
}

/** The latest version of every plan (the terms form offers their codes). */
export function usePlans() {
  const api = useApi();
  return useQuery({
    queryKey: ["operator", "plans"],
    queryFn: async () => (await api.GET("/api/v1/billing/plans")).data?.items ?? [],
    staleTime: 60_000,
  });
}

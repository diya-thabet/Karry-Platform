import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";

export type ReviewPolicy = components["schemas"]["ReviewPolicyResponse"];
export type ReviewPolicyVersion = components["schemas"]["ReviewPolicyVersionResponse"];

export const ItemsReadPermission = "verification.items.read";
export const PoliciesManagePermission = "verification.policies.manage";

export const policiesKey = ["verification", "policies"] as const;

/** The roles an overdue review may escalate to (the API's list, ADR-0037 §5). */
export const escalationRoles = ["site-manager", "tenant-admin", "executive"] as const;

/** Every record type's review policy as the company runs it (ADR-0037 §5). Nothing is asked without the permission. */
export function useReviewPolicies() {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: policiesKey,
    queryFn: async () => (await api.GET("/api/v1/verification/policies")).data?.items ?? [],
    enabled: can(ItemsReadPermission),
  });
}

/** What a person types, checked as the API checks it; null when it is not acceptable (the server checks it again). */
export const policyInput = {
  /** Hours, 0 to 24 with at most one decimal ("1,5" is read as 1.5). */
  hours(text: string): number | null {
    const value = text.trim().replace(",", ".");
    return /^\d{1,2}(\.\d)?$/.test(value) && Number(value) <= 24 ? Number(value) : null;
  },
  /** A whole percentage, 0 to 100, sent as a share (5 → 0.05). */
  percent(text: string): number | null {
    const value = text.trim();
    return /^\d{1,3}$/.test(value) && Number(value) <= 100 ? Number(value) / 100 : null;
  },
  /** Whole hours, 1 to 168 (a week). */
  sla(text: string): number | null {
    const value = text.trim();
    return /^\d{1,3}$/.test(value) && Number(value) >= 1 && Number(value) <= 168 ? Number(value) : null;
  },
};

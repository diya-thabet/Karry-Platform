import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import { useAccess, useApi } from "../app/api";

export type IntegrityRule = components["schemas"]["RuleResponse"];
export type RuleThreshold = components["schemas"]["ThresholdResponse"];

export const FlagsReadPermission = "integrity.flags.read";
export const RulesManagePermission = "integrity.rules.manage";

export const rulesKey = ["integrity", "rules"] as const;

/** Every rule as the company runs it (ADR-0036 §1). Nothing is asked without the permission. */
export function useIntegrityRules() {
  const api = useApi();
  const { can } = useAccess();
  return useQuery({
    queryKey: rulesKey,
    queryFn: async () => (await api.GET("/api/v1/integrity/rules")).data?.items ?? [],
    enabled: can(FlagsReadPermission),
  });
}

/**
 * A typed threshold as the API takes it ("0,5" → "0.5"), kept as text so no precision is lost; null when it is not a
 * plain number within the threshold's range with at most its decimals (the server checks it again).
 */
export function thresholdText(text: string, threshold: RuleThreshold): string | null {
  const value = text.trim().replace(",", ".");
  const decimals = Number(threshold.decimals);
  const pattern = decimals > 0 ? new RegExp(`^\\d+(\\.\\d{1,${String(decimals)}})?$`) : /^\d+$/;
  if (!pattern.test(value)) {
    return null;
  }

  const number = Number(value);
  return number >= Number(threshold.min) && number <= Number(threshold.max) ? value : null;
}

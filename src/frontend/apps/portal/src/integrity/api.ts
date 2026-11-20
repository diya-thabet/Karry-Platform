import { useQuery } from "@tanstack/react-query";
import type { components } from "@karry/api-client";
import type { useTranslation } from "@karry/i18n";
import type { Status } from "@karry/ui";
import { useAccess, useApi } from "../app/api";

export type IntegrityRule = components["schemas"]["RuleResponse"];
export type RuleThreshold = components["schemas"]["ThresholdResponse"];
export type IntegrityFlag = components["schemas"]["IntegrityFlagResponse"];
type TFunction = ReturnType<typeof useTranslation>["t"];

export const FlagsReadPermission = "integrity.flags.read";
export const RulesManagePermission = "integrity.rules.manage";

export const rulesKey = ["integrity", "rules"] as const;
export const flagsKey = ["integrity", "flags"] as const;

/** Severity as a chip: never colour alone, the chip carries the word too. */
export const severityStatus: Record<string, Status> = { low: "pending", medium: "warning", high: "error", critical: "error" };

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

/** The values each rule's explanation names (the details the server keeps on the flag, ADR-0036 §2). */
const explained: Record<string, readonly string[]> = {
  "RULE-FO-01": ["meterKind", "expectedValue", "foundValue", "difference"],
  "RULE-FO-02": ["meterKind", "startValue", "endValue", "delta", "minutes", "toleranceHours"],
  "RULE-FO-03": ["part", "minutesAhead"],
  "RULE-FO-04": ["part", "metresOutside", "accuracyMetres", "marginMetres"],
  "RULE-FO-05": ["part"],
  "RULE-FO-06": ["part", "otherEntries"],
  "RULE-FO-07": ["type"],
  "RULE-FO-09": ["part", "reason"],
};

/**
 * What a flag found, in words, with the values it was raised on (US-P2-INT-01 AC2); a flag without them (an older
 * rule version, an unknown rule) falls back to what the rule checks.
 */
export function flagExplanation(t: TFunction, flag: IntegrityFlag): string {
  const d = flag.details;
  const needed = explained[flag.rule];
  if (!needed || needed.some((key) => d[key] === undefined)) {
    return t(`portal.integrity.rule.${flag.rule}.description`, { defaultValue: flag.rule });
  }

  const n = (key: string) => Number(d[key]);
  const unit = d.meterKind === "hours" ? "h" : "km";
  const part = d.part === "start" ? "start" : "end";
  switch (flag.rule) {
    case "RULE-FO-01":
      return t("portal.integrity.flag.RULE-FO-01", { found: n("foundValue"), expected: n("expectedValue"), difference: n("difference"), unit });
    case "RULE-FO-02":
      return t("portal.integrity.flag.RULE-FO-02", {
        start: n("startValue"), end: n("endValue"), delta: n("delta"), minutes: n("minutes"), tolerance: n("toleranceHours"), unit,
      });
    case "RULE-FO-03":
      return t("portal.integrity.flag.RULE-FO-03", { part, minutes: Math.abs(n("minutesAhead")), direction: n("minutesAhead") < 0 ? "behind" : "ahead" });
    case "RULE-FO-04":
      return t("portal.integrity.flag.RULE-FO-04", { part, outside: n("metresOutside"), accuracy: n("accuracyMetres"), margin: n("marginMetres") });
    case "RULE-FO-05":
      return t("portal.integrity.flag.RULE-FO-05", { part });
    case "RULE-FO-06":
      return d.sameAsStart === "true"
        ? t("portal.integrity.flag.RULE-FO-06.sameAsStart")
        : t("portal.integrity.flag.RULE-FO-06", { part, others: n("otherEntries") });
    case "RULE-FO-09":
      return t("portal.integrity.flag.RULE-FO-09", { part, reason: d.reason ?? "", deactivatedAt: new Date(d.deactivatedAt ?? 0) });
    default:
      return t("portal.integrity.flag.RULE-FO-07", { type: d.type ?? "" });
  }
}

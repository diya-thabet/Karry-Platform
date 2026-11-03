import { useBooleanFlagValue } from "@openfeature/react-sdk";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "@karry/i18n";
import { useUsage } from "../app/api";

/**
 * Shown to billing readers while the company is above a plan limit (US-P1-BIL-04 AC2). Above the limit nothing is
 * blocked yet; at the ceiling only adding more is.
 */
export function UsageBanner() {
  const { t } = useTranslation();
  const { items } = useUsage();
  const shown = useBooleanFlagValue("portal.usage-banner", true); // kill switch (ADR-0019)
  const over = items.filter((line) => line.state !== "within");
  if (!shown || over.length === 0) {
    return null;
  }

  const blocked = over.some((line) => line.state === "blocked");
  return (
    <div role="status" className="border-b-[length:var(--karry-border-width)] border-warning bg-surface px-6 py-3">
      <p className="font-semibold">
        {t(blocked ? "portal.plan.bannerBlocked" : "portal.plan.bannerOver", {
          metrics: over.map((line) => t(`metrics.${line.metric}`, { defaultValue: line.metric })).join(", "),
        })}{" "}
        <Link to="/settings/plan" className="text-primary underline underline-offset-4">{t("portal.plan.title")}</Link>
      </p>
    </div>
  );
}

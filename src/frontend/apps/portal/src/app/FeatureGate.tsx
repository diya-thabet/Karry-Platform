import type { ReactNode } from "react";
import { useTranslation } from "@karry/i18n";
import { UpgradeCard } from "@karry/ui";
import { useEntitlement } from "./api";

/**
 * Shows its children only when the company's plan includes `feature`; otherwise the upgrade card (US-P1-BIL-03 AC2,
 * TC-P1-BIL-003). Nothing while the plan is loading, so a locked feature never flashes.
 */
export function FeatureGate({ feature, children, action }: { feature: string; children: ReactNode; action?: ReactNode }) {
  const { t } = useTranslation();
  const { loaded, included } = useEntitlement(feature);
  if (!loaded) {
    return null;
  }

  if (included) {
    return <>{children}</>;
  }

  const name = t(`features.${feature}`, { defaultValue: feature });
  return (
    <UpgradeCard title={t("upgrade.title", { feature: name })} action={action}>
      {t("upgrade.body")}
    </UpgradeCard>
  );
}

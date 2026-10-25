import { Link, useRouterState } from "@tanstack/react-router";
import type { ReactNode } from "react";
import { useTranslation } from "@karry/i18n";
import { EmptyState } from "@karry/ui";
import { BillingReadPermission, endedStates, useAccess, useEntitlements } from "../api";

/**
 * What the subscription state means for the person (US-P1-BIL-05): a payment reminder for billing readers while
 * past due, and for everyone what is paused while restricted or suspended. The API enforces it either way.
 */
export function SubscriptionNotice() {
  const { t } = useTranslation();
  const { status } = useEntitlements();
  const { can } = useAccess();
  const reader = can(BillingReadPermission);
  const key =
    status === "restricted" ? "portal.subscription.restricted"
    : status === "suspended" ? "portal.subscription.suspended"
    : status === "past_due" && reader ? "portal.subscription.pastDue"
    : undefined;
  if (!key) {
    return null;
  }

  return (
    <div role="status" className="border-b-[length:var(--karry-border-width)] border-warning bg-surface px-6 py-3">
      <p className="font-semibold">
        {t(key)}{" "}
        {reader && <Link to="/settings/plan" className="text-primary underline underline-offset-4">{t("portal.plan.title")}</Link>}
      </p>
    </div>
  );
}

/**
 * Once the subscription has ended (US-P1-BIL-05 AC3), the portal shows only what remains: the explanation, and
 * "Plan & usage" for billing readers. Everything else answers `billing.subscription_inactive` anyway.
 */
export function SubscriptionGate({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const { status } = useEntitlements();
  const { can } = useAccess();
  const path = useRouterState({ select: (state) => state.location.pathname });
  if (!status || !endedStates.has(status) || (path === "/settings/plan" && can(BillingReadPermission))) {
    return <>{children}</>;
  }

  return (
    <EmptyState
      title={t("portal.subscription.endedTitle")}
      action={can(BillingReadPermission) ? <Link to="/settings/plan" className="text-primary underline underline-offset-4">{t("portal.plan.title")}</Link> : undefined}
    >
      {t("portal.subscription.endedBody")}
    </EmptyState>
  );
}

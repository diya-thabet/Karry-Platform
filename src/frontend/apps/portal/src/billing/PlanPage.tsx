import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "@karry/i18n";
import { DataTable, EmptyState, PageHeader, StatusChip, type Status } from "@karry/ui";
import { useApi, useEntitlements, useUsage, type UsageLine as Line } from "../app/api";
import { Trail } from "../shell/Trail";

const chips: Record<string, Status> = { within: "ok", over: "warning", blocked: "error" };

/**
 * "Plan & usage" (US-P1-BIL-04 AC3): the company's plan, its state and what it uses against each limit. Changing the
 * plan goes through Karry until the billing provider (Phase 10).
 */
export function PlanPage() {
  const { t } = useTranslation();
  const api = useApi();
  const { planCode, includes } = useEntitlements();
  const subscription = useQuery({ queryKey: ["billing", "subscription"], queryFn: async () => (await api.GET("/api/v1/billing/subscription")).data });
  const usage = useUsage();
  const status = subscription.data?.status;

  return (
    <div className="flex flex-col gap-6">
      <PageHeader breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />} title={t("portal.plan.title")} />
      <section aria-labelledby="plan-heading" className="flex flex-col gap-2 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
        <h2 id="plan-heading" className="text-base font-semibold">
          {planCode ? t(`plans.${planCode}`, { defaultValue: planCode }) : t("common.loading")}
        </h2>
        {status && (
          <p>
            <StatusChip status={status === "active" || status === "trial" ? "ok" : "warning"}>
              {t(`portal.plan.status.${status}`, { defaultValue: status })}
            </StatusChip>
          </p>
        )}
        {status === "trial" && subscription.data?.trialEndsAt && (
          <p className="text-text-muted">{t("portal.plan.trialEnds", { date: new Date(subscription.data.trialEndsAt) })}</p>
        )}
        {subscription.data && <p className="text-text-muted">{t("portal.plan.sites", { count: subscription.data.sites })}</p>}
        <p className="text-text-muted">{t("portal.plan.change")}</p>
      </section>

      <DataTable<Line>
        caption={t("portal.plan.usage")}
        rows={usage.items}
        rowKey={(line) => line.metric}
        loading={!usage.loaded}
        empty={<EmptyState title={t("common.empty.title")} />}
        columns={[
          { key: "metric", header: t("portal.plan.metric"), cell: (line) => t(`metrics.${line.metric}`, { defaultValue: line.metric }) },
          { key: "usage", header: t("portal.plan.used"), cell: (line) => line.usage },
          { key: "limit", header: t("portal.plan.limit"), cell: (line) => line.limit ?? t("portal.plan.unlimited") },
          {
            key: "state",
            header: t("portal.plan.state"),
            cell: (line) => <StatusChip status={chips[line.state] ?? "pending"}>{t(`portal.plan.state.${line.state}`, { defaultValue: line.state })}</StatusChip>,
          },
        ]}
      />
      {usage.items.some((line) => line.state !== "within") && <p>{t("portal.plan.overExplained")}</p>}
      {usage.snapshotOn && <p className="text-text-muted">{t("portal.plan.snapshotOn", { date: new Date(usage.snapshotOn) })}</p>}

      <section aria-labelledby="features-heading" className="flex flex-col gap-2 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
        <h2 id="features-heading" className="text-base font-semibold">{t("portal.plan.features")}</h2>
        <ul className="grid gap-1 md:grid-cols-2">
          {featureCodes.filter(includes).map((code) => (
            <li key={code}>{t(`features.${code}`)}</li>
          ))}
        </ul>
      </section>
    </div>
  );
}

const featureCodes = [
  "field-operations", "weighbridge", "fuel", "integrity-dashboard", "inventory", "inventory.multi-store", "maintenance", "plant",
  "finance", "ai.copilot", "edge.agent", "telematics", "sso", "api.read", "api.write", "dedicated-hosting",
];

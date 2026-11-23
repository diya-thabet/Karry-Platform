import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Card, Checkbox, DataTable, EmptyState, ErrorState, PageHeader, PageSkeleton, SelectField, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { Trail } from "../shell/Trail";
import { escalationRoles, PoliciesManagePermission, policiesKey, policyInput, useReviewPolicies, type ReviewPolicy, type ReviewPolicyVersion } from "./api";

/**
 * How the company reviews its records (US-P2-VER-05, ADR-0037 §5): auto-approval of entries without flags, when a
 * correction needs a second approver, how long a review may wait and who it escalates to. A company administrator
 * publishes a new version; entries already waiting keep the version they started under.
 */
export function PoliciesPage() {
  const { t } = useTranslation();
  const policies = useReviewPolicies();

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.reviewPolicies.title")}
        description={t("portal.reviewPolicies.description")}
      />
      {policies.isPending ? (
        <PageSkeleton />
      ) : policies.isError ? (
        <ErrorState onRetry={() => void policies.refetch()} />
      ) : policies.data.length === 0 ? (
        <EmptyState title={t("portal.reviewPolicies.empty")} />
      ) : (
        policies.data.map((policy) => <PolicyCard key={policy.recordType} policy={policy} />)
      )}
    </div>
  );
}

function PolicyCard({ policy }: { policy: ReviewPolicy }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const editable = can(PoliciesManagePermission);
  const current = policy.current;
  const number = (value: number | string) => new Intl.NumberFormat(intlLocale(i18n.language)).format(Number(value));
  const [autoApprove, setAutoApprove] = useState(current.autoApprove);
  const [hours, setHours] = useState(String(current.fourEyesHours));
  const [percent, setPercent] = useState(String(Math.round(Number(current.fourEyesShare) * 100)));
  const [sla, setSla] = useState(String(current.slaHours));
  const [escalateTo, setEscalateTo] = useState(current.escalateTo);
  const parsed = { hours: policyInput.hours(hours), share: policyInput.percent(percent), sla: policyInput.sla(sla) };
  const valid = parsed.hours !== null && parsed.share !== null && parsed.sla !== null;

  const publish = useMutation({
    mutationFn: () => withStepUp(async () => (await api.PUT("/api/v1/verification/policies/{recordType}", {
      params: { path: { recordType: policy.recordType } },
      body: { autoApprove, fourEyesHours: parsed.hours ?? 0, fourEyesShare: parsed.share ?? 0, slaHours: parsed.sla ?? 0, escalateTo, effectiveFrom: null },
    })).data),
    onSuccess: async (published) => {
      if (!published) {
        return;
      }

      await queryClient.invalidateQueries({ queryKey: policiesKey });
      toast({ status: "ok", title: t("portal.reviewPolicies.published", { version: published.version }) });
    },
  });

  const at = (value: string | null | undefined) => (value
    ? new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value))
    : "—");

  return (
    <Card>
      <form className="flex flex-col gap-4" noValidate aria-label={t(`portal.reviewPolicies.type.${policy.recordType}`, { defaultValue: policy.recordType })}
        onSubmit={(event) => { event.preventDefault(); if (valid) { publish.mutate(); } }}>
        <div className="flex flex-col gap-1">
          <h2 className="text-lg font-semibold">{t(`portal.reviewPolicies.type.${policy.recordType}`, { defaultValue: policy.recordType })}</h2>
          <p className="text-sm text-text-muted">
            {Number(current.version) === 0
              ? t("portal.reviewPolicies.defaults")
              : t("portal.reviewPolicies.inForce", { version: current.version, at: at(current.effectiveFrom), by: current.publishedBy ?? "—" })}
          </p>
        </div>

        <div className="flex flex-col gap-1">
          <Checkbox label={t("portal.reviewPolicies.autoApprove")} checked={autoApprove} disabled={!editable}
            onChange={(event) => { setAutoApprove(event.currentTarget.checked); }} />
          <p className="text-sm text-text-muted">{t("portal.reviewPolicies.autoApproveHint")}</p>
        </div>
        <div className="flex flex-wrap gap-4">
          <TextField className="max-w-48" inputMode="decimal" label={t("portal.reviewPolicies.fourEyesHours")} description={t("portal.reviewPolicies.fourEyesHoursHint")}
            value={hours} disabled={!editable} error={parsed.hours === null ? t("portal.reviewPolicies.hoursInvalid") : undefined}
            onChange={(event) => { setHours(event.currentTarget.value); }} />
          <TextField className="max-w-48" inputMode="numeric" label={t("portal.reviewPolicies.fourEyesPercent")} description={t("portal.reviewPolicies.fourEyesPercentHint")}
            value={percent} disabled={!editable} error={parsed.share === null ? t("portal.reviewPolicies.percentInvalid") : undefined}
            onChange={(event) => { setPercent(event.currentTarget.value); }} />
          <TextField className="max-w-48" inputMode="numeric" label={t("portal.reviewPolicies.slaHours")} description={t("portal.reviewPolicies.slaHoursHint")}
            value={sla} disabled={!editable} error={parsed.sla === null ? t("portal.reviewPolicies.slaInvalid") : undefined}
            onChange={(event) => { setSla(event.currentTarget.value); }} />
          <SelectField label={t("portal.reviewPolicies.escalateTo")} value={escalateTo} disabled={!editable} onValueChange={setEscalateTo}
            options={escalationRoles.map((role) => ({ value: role, label: t(`roles.${role}`, { defaultValue: role }) }))} />
        </div>
        {editable && (
          <div>
            <Button type="submit" loading={publish.isPending} disabled={!valid}>{t("portal.reviewPolicies.publish")}</Button>
            <p className="mt-2 text-sm text-text-muted">{t("portal.reviewPolicies.publishHint")}</p>
          </div>
        )}

        {policy.versions.length > 0 && (
          <DataTable<ReviewPolicyVersion>
            caption={t("portal.reviewPolicies.history")}
            rows={[...policy.versions].reverse()}
            rowKey={(v) => String(v.version)}
            empty={<EmptyState title={t("portal.reviewPolicies.defaults")} />}
            columns={[
              { key: "version", header: t("portal.reviewPolicies.version"), cell: (v) => number(v.version) },
              { key: "from", header: t("portal.reviewPolicies.from"), cell: (v) => at(v.effectiveFrom) },
              { key: "auto", header: t("portal.reviewPolicies.autoApprove"), cell: (v) => t(v.autoApprove ? "portal.reviewPolicies.yes" : "portal.reviewPolicies.no") },
              { key: "sla", header: t("portal.reviewPolicies.slaHours"), cell: (v) => number(v.slaHours) },
              { key: "by", header: t("portal.reviewPolicies.by"), cell: (v) => v.publishedBy ?? "—" },
            ]}
          />
        )}
      </form>
    </Card>
  );
}

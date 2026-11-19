import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { Lock } from "lucide-react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Card, Checkbox, EmptyState, ErrorState, PageHeader, PageSkeleton, StatusChip, TextField, useToast, type Status } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { VersionConflict } from "../app/VersionConflict";
import { handlesVersionConflict, isVersionConflict } from "../app/conflicts";
import { useRecordDraft } from "../app/recordDraft";
import { Trail } from "../shell/Trail";
import { rulesKey, RulesManagePermission, thresholdText, useIntegrityRules, type IntegrityRule } from "./api";

const severityStatus: Record<string, Status> = { low: "pending", medium: "warning", high: "error", critical: "error" };

/**
 * The company's integrity rules (ADR-0036 §1, US-P2-INT-01 AC3): what each rule checks, whether it runs and its
 * thresholds. A company administrator switches a rule off or changes a threshold; the security rules always run.
 */
export function RulesPage() {
  const { t } = useTranslation();
  const rules = useIntegrityRules();

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.integrity.rules.title")}
        description={t("portal.integrity.rules.description")}
      />
      {rules.isPending ? (
        <PageSkeleton />
      ) : rules.isError ? (
        <ErrorState onRetry={() => void rules.refetch()} />
      ) : rules.data.length === 0 ? (
        <EmptyState title={t("portal.integrity.rules.empty")} />
      ) : (
        <ul className="flex flex-col gap-4">
          {rules.data.map((rule) => (
            <li key={rule.rule}>
              <RuleForm rule={rule} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

interface RuleDraft {
  enabled: boolean;
  values: Record<string, string>;
}

const toDraft = (rule: IntegrityRule | null): RuleDraft => ({
  enabled: rule?.enabled ?? true,
  values: Object.fromEntries((rule?.thresholds ?? []).map((threshold) => [threshold.name, String(threshold.value)])),
});

function RuleForm({ rule }: { rule: IntegrityRule }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const editable = can(RulesManagePermission);
  // The switch and thresholds, and the version they were filled from: a save is applied to it only (If-Match, H-C1).
  const { draft, setDraft, version, accept } = useRecordDraft(rule, toDraft);
  const [conflict, setConflict] = useState(false);
  const number = (value: number | string) => new Intl.NumberFormat(intlLocale(i18n.language)).format(Number(value));
  const parsed = rule.thresholds.map((threshold) => ({ threshold, text: thresholdText(draft.values[threshold.name] ?? "", threshold) }));
  const valid = parsed.every((p) => p.text !== null);

  const refresh = (saved: IntegrityRule) => {
    queryClient.setQueryData<IntegrityRule[]>(rulesKey, (rules) => rules?.map((r) => (r.rule === saved.rule ? saved : r)));
  };

  const latest = async () => (await api.GET("/api/v1/integrity/rules")).data?.items.find((r) => r.rule === rule.rule);

  const save = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (expected?: string) => withStepUp(async () => {
      const tag = expected ?? version;
      return (await api.PUT("/api/v1/integrity/rules/{rule}", {
        params: { path: { rule: rule.rule }, header: tag ? { "If-Match": tag } : {} },
        body: { enabled: rule.security || draft.enabled, thresholds: Object.fromEntries(parsed.map((p) => [p.threshold.name, p.text ?? ""])) },
      })).data;
    }),
    onError: (error) => { setConflict(isVersionConflict(error)); },
    onSuccess: (saved) => {
      if (!saved) {
        return;
      }

      accept(saved);
      setConflict(false);
      refresh(saved);
      toast({ status: "ok", title: t("portal.integrity.rules.saved") });
    },
  });

  // The person keeps their entries over the other change: saved on the newest version.
  const keepMine = async () => {
    const current = await latest();
    if (current) {
      save.mutate(current.version);
    }
  };
  // The person chose the other change: the form shows it and their entries are dropped.
  const showTheirs = async () => {
    const current = await latest();
    if (current) {
      accept(current);
      setConflict(false);
      refresh(current);
    }
  };

  const title = t(`portal.integrity.rule.${rule.rule}.title`, { defaultValue: rule.rule });
  return (
    <Card>
      <form className="flex flex-col gap-4" noValidate aria-label={title} onSubmit={(event) => { event.preventDefault(); if (valid) { save.mutate(undefined); } }}>
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="flex flex-col gap-1">
            <h2 className="text-lg font-semibold">
              {title} <span className="text-sm font-normal text-text-muted" dir="ltr">{rule.rule}</span>
            </h2>
            <p className="text-text-muted">{t(`portal.integrity.rule.${rule.rule}.description`, { defaultValue: "" })}</p>
          </div>
          <StatusChip status={severityStatus[rule.severity] ?? "pending"}>
            {t(`portal.integrity.severity.${rule.severity}`, { defaultValue: rule.severity })}
          </StatusChip>
        </div>

        {rule.security ? (
          <p className="flex items-center gap-2 text-sm text-text-muted">
            <Lock aria-hidden className="size-4" />
            {t("portal.integrity.rules.locked")}
          </p>
        ) : (
          <Checkbox
            label={t("portal.integrity.rules.enabled")}
            checked={draft.enabled}
            disabled={!editable}
            onChange={(event) => { const enabled = event.currentTarget.checked; setDraft((current) => ({ ...current, enabled })); }}
          />
        )}

        {parsed.map(({ threshold, text }) => (
          <TextField
            key={threshold.name}
            className="max-w-40"
            inputMode={Number(threshold.decimals) > 0 ? "decimal" : "numeric"}
            label={`${t(`portal.integrity.threshold.${threshold.name}`, { defaultValue: threshold.name })} (${threshold.unit})`}
            description={t("portal.integrity.rules.range", {
              min: number(threshold.min), max: number(threshold.max), unit: threshold.unit, default: number(threshold.default),
            })}
            error={text === null ? t("portal.integrity.rules.outOfRange", { min: number(threshold.min), max: number(threshold.max), unit: threshold.unit }) : undefined}
            value={draft.values[threshold.name] ?? ""}
            disabled={!editable}
            onChange={(event) => {
              const value = event.currentTarget.value;
              setDraft((current) => ({ ...current, values: { ...current.values, [threshold.name]: value } }));
            }}
          />
        ))}

        {rule.changedAt && (
          <p className="text-sm text-text-muted">
            {t("portal.integrity.rules.changedAt", {
              when: new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(rule.changedAt)),
            })}
          </p>
        )}

        {conflict && <VersionConflict busy={save.isPending} onKeepMine={() => void keepMine()} onShowTheirs={() => void showTheirs()} />}

        {editable && !(rule.security && rule.thresholds.length === 0) && (
          <div>
            <Button type="submit" loading={save.isPending} disabled={!valid}>{t("portal.integrity.rules.save")}</Button>
          </div>
        )}
      </form>
    </Card>
  );
}

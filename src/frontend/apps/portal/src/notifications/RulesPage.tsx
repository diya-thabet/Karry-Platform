import { useMutation, useQueryClient } from "@tanstack/react-query";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Card, Checkbox, EmptyState, ErrorState, PageHeader, PageSkeleton, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { Trail } from "../shell/Trail";
import { NoticeRulesManagePermission, noticeRulesKey, noticeTypeName, useNoticeRules, type NoticeRule } from "./api";

/**
 * The company's notice rules (ADR-0039 §2): each kind of notice, who hears of it, by which channels, and whether the
 * company sends it. Switching one off silences an alert for everyone, so it asks for the password again; switching it
 * back on does not. Each change is in the audit log with who made it.
 */
export function NoticeRulesPage() {
  const { t } = useTranslation();
  const rules = useNoticeRules();

  return (
    <div className="flex flex-col gap-6">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />}
        title={t("portal.noticeRules.title")}
        description={t("portal.noticeRules.description")}
      />
      {rules.isPending ? (
        <PageSkeleton />
      ) : rules.isError ? (
        <ErrorState onRetry={() => void rules.refetch()} />
      ) : rules.data.length === 0 ? (
        <EmptyState title={t("portal.noticeRules.empty")} />
      ) : (
        <ul className="flex flex-col gap-4">
          {rules.data.map((rule) => (
            <li key={rule.type}>
              <RuleCard rule={rule} />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function RuleCard({ rule }: { rule: NoticeRule }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const queryClient = useQueryClient();
  const toast = useToast();
  const { can } = useAccess();
  const editable = can(NoticeRulesManagePermission);
  const name = noticeTypeName(t, rule.type);

  const save = useMutation({
    mutationFn: async (enabled: boolean) => withStepUp(async () =>
      (await api.PUT("/api/v1/notifications/rules/{noticeType}", { params: { path: { noticeType: rule.type } }, body: { enabled } })).data),
    onSuccess: (saved) => {
      if (!saved) {
        return;
      }

      queryClient.setQueryData<NoticeRule[]>(noticeRulesKey, (rules) => rules?.map((r) => (r.type === saved.type ? saved : r)));
      toast({ status: "ok", title: saved.enabled ? t("portal.noticeRules.switchedOn") : t("portal.noticeRules.switchedOff") });
    },
  });

  return (
    <Card>
      <section className="flex flex-col gap-3" aria-label={name}>
        <div className="flex flex-col gap-1">
          <h2 className="text-lg font-semibold">{name}</h2>
          <p className="text-text-muted">{t(`portal.noticeRules.audience.${rule.type}`, { defaultValue: "" })}</p>
          <p className="text-sm text-text-muted">
            {t("portal.noticeRules.channels", {
              channels: new Intl.ListFormat(intlLocale(i18n.language), { type: "conjunction" })
                .format(rule.channels.map((channel) => t(`portal.notices.channel.${channel}`, { defaultValue: channel }))),
            })}
          </p>
        </div>
        <Checkbox
          label={t("portal.noticeRules.enabled")}
          aria-label={`${name}: ${t("portal.noticeRules.enabled")}`}
          checked={rule.enabled}
          disabled={!editable || save.isPending}
          onChange={(event) => { save.mutate(event.currentTarget.checked); }}
        />
        {rule.changedAt && (
          <p className="text-sm text-text-muted">
            {t("portal.noticeRules.changed", {
              when: new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(rule.changedAt)),
              who: rule.changedBy ?? "",
            })}
          </p>
        )}
      </section>
    </Card>
  );
}

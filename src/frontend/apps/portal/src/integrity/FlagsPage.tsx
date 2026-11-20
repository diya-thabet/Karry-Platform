import { useInfiniteQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, ErrorState, PageHeader, SelectField, StatusChip } from "@karry/ui";
import { useAccess, useApi } from "../app/api";
import { useSiteNames } from "../assets/api";
import { Trail } from "../shell/Trail";
import { FlagsReadPermission, flagExplanation, flagsKey, RulesManagePermission, severityStatus, type IntegrityFlag } from "./api";

const statuses = ["open", "closed", "all"] as const;
type FlagStatus = (typeof statuses)[number];
const rules = ["RULE-FO-01", "RULE-FO-02", "RULE-FO-03", "RULE-FO-04", "RULE-FO-05", "RULE-FO-06", "RULE-FO-07", "RULE-FO-09"] as const;
const subjects = ["shift_entry", "meter", "sync_command"] as const;
const any = "any";
const pageSize = 50;

function useFlags(status: FlagStatus, siteId: string, rule: string) {
  const api = useApi();
  const { can } = useAccess();
  return useInfiniteQuery({
    queryKey: [...flagsKey, { status, siteId, rule }],
    queryFn: async ({ pageParam }) => (await api.GET("/api/v1/integrity/flags", {
      params: {
        query: {
          status,
          limit: pageSize,
          ...(siteId ? { siteId } : {}),
          ...(rule ? { rule } : {}),
          ...(pageParam ? { cursor: pageParam } : {}),
        },
      },
    })).data ?? { items: [], nextCursor: null },
    initialPageParam: "",
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: can(FlagsReadPermission),
  });
}

/**
 * The integrity flags of the caller's sites (US-P2-INT-01 AC2, ADR-0036 §2), read-only: what each rule found, in words,
 * with its severity, site and evidence; settling a flag is the controller console of P2.8.
 */
export function FlagsPage() {
  const { t, i18n } = useTranslation();
  const { can } = useAccess();
  const { sites, name } = useSiteNames();
  const [status, setStatus] = useState<FlagStatus>("open");
  const [siteId, setSiteId] = useState("");
  const [rule, setRule] = useState("");
  const flags = useFlags(status, siteId, rule);
  const rows = flags.data?.pages.flatMap((page) => page.items) ?? [];
  const at = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const subject = (flag: IntegrityFlag) => (subjects as readonly string[]).includes(flag.subject.type)
    ? t(`portal.integrity.flags.subject.${flag.subject.type}`)
    : flag.subject.type;

  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/assets", label: t("portal.nav.assets") }]} />}
        title={t("portal.integrity.flags.title")}
        description={t("portal.integrity.flags.description")}
        actions={can(RulesManagePermission) && (
          <Button variant="secondary" asChild>
            <Link to="/settings/integrity">{t("portal.integrity.rules.title")}</Link>
          </Button>
        )}
      />
      <div className="flex flex-wrap items-end gap-3">
        <SelectField label={t("portal.integrity.flags.status")} value={status} onValueChange={(value) => { setStatus(value as FlagStatus); }}
          options={statuses.map((value) => ({ value, label: t(`portal.integrity.flags.status.${value}`) }))} />
        <SelectField label={t("portal.integrity.flags.site")} value={siteId || any} onValueChange={(value) => { setSiteId(value === any ? "" : value); }}
          options={[{ value: any, label: t("portal.integrity.flags.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]} />
        <SelectField label={t("portal.integrity.flags.rule")} value={rule || any} onValueChange={(value) => { setRule(value === any ? "" : value); }}
          options={[{ value: any, label: t("portal.integrity.flags.anyRule") }, ...rules.map((code) => ({ value: code, label: t(`portal.integrity.rule.${code}.title`) }))]} />
      </div>
      {flags.isError ? (
        <ErrorState onRetry={() => void flags.refetch()} />
      ) : (
        <DataTable<IntegrityFlag>
          caption={t("portal.integrity.flags.title")}
          rows={rows}
          rowKey={(flag) => flag.id}
          loading={flags.isPending && flags.fetchStatus !== "idle"}
          empty={<EmptyState title={t(status === "open" ? "portal.integrity.flags.noneOpen" : "portal.integrity.flags.none")} />}
          columns={[
            {
              key: "rule",
              header: t("portal.integrity.flags.rule"),
              cell: (flag) => (
                <span className="flex flex-col items-start gap-1">
                  <span className="font-semibold">{t(`portal.integrity.rule.${flag.rule}.title`, { defaultValue: flag.rule })}</span>
                  <StatusChip status={severityStatus[flag.severity] ?? "warning"}>{t(`portal.integrity.severity.${flag.severity}`, { defaultValue: flag.severity })}</StatusChip>
                </span>
              ),
            },
            { key: "found", header: t("portal.integrity.flags.found"), cell: (flag) => flagExplanation(t, flag) },
            {
              key: "about",
              header: t("portal.integrity.flags.about"),
              cell: (flag) => (
                <span className="flex flex-col gap-1">
                  <span>{subject(flag)}</span>
                  {flag.evidence.length > 0 && (
                    <span className="text-sm text-text-muted">{t("portal.integrity.flags.evidence", { count: flag.evidence.length })}</span>
                  )}
                </span>
              ),
            },
            { key: "site", header: t("portal.integrity.flags.site"), cell: (flag) => (flag.siteId ? name(flag.siteId) : "—") },
            { key: "opened", header: t("portal.integrity.flags.opened"), cell: (flag) => at(flag.openedAt) },
            {
              key: "state",
              header: t("portal.integrity.flags.state"),
              cell: (flag) => (flag.closedAt
                ? (
                    <span className="flex flex-col gap-1">
                      <StatusChip status="ok">{t("portal.integrity.flags.closedAt", { at: new Date(flag.closedAt) })}</StatusChip>
                      {flag.closeReason && (
                        <span className="text-sm text-text-muted">
                          {flag.closeReason === "re-evaluated" ? t("portal.integrity.flags.reevaluated") : flag.closeReason}
                        </span>
                      )}
                    </span>
                  )
                : <StatusChip status="warning">{t("portal.integrity.flags.open")}</StatusChip>),
            },
          ]}
        />
      )}
      {flags.hasNextPage && (
        <div className="flex justify-center">
          <Button variant="secondary" loading={flags.isFetchingNextPage} onClick={() => void flags.fetchNextPage()}>{t("portal.integrity.flags.more")}</Button>
        </div>
      )}
    </div>
  );
}

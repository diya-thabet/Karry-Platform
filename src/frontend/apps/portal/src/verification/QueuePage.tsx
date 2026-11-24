import { useState } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Checkbox, DataTable, EmptyState, ErrorState, PageHeader, SelectField, StatusChip } from "@karry/ui";
import { useSiteNames } from "../assets/api";
import { EntryPhoto } from "./EntryPhoto";
import { ReviewDrawer } from "./ReviewDrawer";
import { entryFlags, flagKey, queueSize, useMachineCodes, useReviewQueue, type QueueFilters, type QueueRow } from "./queue";

const any = "any";

/**
 * The controller's queue (US-P2-VER-01, ADR-0038): the entries of the person's sites waiting for a decision, overdue
 * first, then flagged, then the oldest; each with its machine and shift, its meter values and Δ, its photos and its
 * flags in words. It refreshes itself every 5 s while the page is open.
 */
export function QueuePage() {
  const { t, i18n } = useTranslation();
  const { sites, name } = useSiteNames();
  const code = useMachineCodes();
  const [filters, setFilters] = useState<QueueFilters>({ siteId: "", flag: "", overdue: false });
  const queue = useReviewQueue(filters);
  const [open, setOpen] = useState<QueueRow | null>(null);
  const rows = queue.data ?? [];
  const locale = intlLocale(i18n.language);
  const at = (value: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  // Decimals come as JSON numbers or strings (exact values, ADR-0034): formatted for the reader, one decimal.
  const number = (value: number | string) => new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(Number(value));

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={t("portal.review.title")} description={t("portal.review.description")} />
      <div className="flex flex-wrap items-end gap-3">
        <SelectField label={t("portal.review.site")} value={filters.siteId || any} onValueChange={(value) => { setFilters({ ...filters, siteId: value === any ? "" : value }); }}
          options={[{ value: any, label: t("portal.review.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]} />
        <SelectField label={t("portal.review.flag")} value={filters.flag || any} onValueChange={(value) => { setFilters({ ...filters, flag: value === any ? "" : value }); }}
          options={[{ value: any, label: t("portal.review.anyFlag") }, ...entryFlags.map((flag) => ({ value: flag, label: t(`portal.review.flag.${flag}`) }))]} />
        <Checkbox label={t("portal.review.overdueOnly")} checked={filters.overdue} onChange={(event) => { setFilters({ ...filters, overdue: event.target.checked }); }} />
      </div>
      {queue.isError ? (
        <ErrorState onRetry={() => void queue.refetch()} />
      ) : (
        <DataTable<QueueRow>
          caption={t("portal.review.title")}
          rows={rows}
          rowKey={(row) => row.item.id}
          loading={queue.isPending && queue.fetchStatus !== "idle"}
          empty={<EmptyState title={t("portal.review.empty")} />}
          columns={[
            {
              key: "machine",
              header: t("portal.review.machine"),
              cell: ({ item, entry }) => (
                <span className="flex flex-col gap-1">
                  <span className="font-semibold">{code(item.assetId)}</span>
                  <span className="text-sm text-text-muted">
                    {entry?.shift ? t("portal.review.shift", { date: new Date(`${entry.shift.date}T12:00:00Z`), code: entry.shift.code }) : at(item.coveredFrom)}
                  </span>
                  <span className="text-sm text-text-muted">{name(item.siteId)}</span>
                </span>
              ),
            },
            {
              key: "meter",
              header: t("portal.review.meter"),
              cell: ({ entry }) => (entry
                ? (
                    <span className="flex flex-col gap-1">
                      <span>{t("portal.review.values", { start: number(entry.startValue), end: entry.endValue === null ? "—" : number(entry.endValue), unit: entry.unit })}</span>
                      {entry.usage !== null && <span className="font-semibold">{t("portal.review.delta", { delta: number(entry.usage), unit: entry.unit })}</span>}
                    </span>
                  )
                : "—"),
            },
            {
              key: "photos",
              header: t("portal.review.photos"),
              cell: ({ entry }) => (entry
                ? (
                    <span className="flex gap-2">
                      <EntryPhoto entryId={entry.id} sha256={entry.startPhoto} label={t("portal.review.startPhoto")} />
                      {entry.endPhoto && <EntryPhoto entryId={entry.id} sha256={entry.endPhoto} label={t("portal.review.endPhoto")} />}
                    </span>
                  )
                : "—"),
            },
            {
              key: "flags",
              header: t("portal.review.flags"),
              cell: ({ item }) => (item.flags.length === 0
                ? <span className="text-text-muted">{t("portal.review.noFlags")}</span>
                : (
                    <ul className="flex flex-col gap-1">
                      {item.flags.map((flag) => {
                        const key = flagKey(flag);
                        return <li key={flag}>{key ? t(key) : flag}</li>;
                      })}
                    </ul>
                  )),
            },
            {
              key: "status",
              header: t("portal.review.status"),
              cell: ({ item }) => (
                <span className="flex flex-col items-start gap-1">
                  {item.escalatedAt
                    ? <StatusChip status="error">{t("portal.review.overdue")}</StatusChip>
                    : <span className="text-sm text-text-muted">{t("portal.review.due", { at: new Date(item.dueAt) })}</span>}
                  {item.status === "awaiting_second" && <StatusChip status="warning">{t("portal.review.awaitingSecond")}</StatusChip>}
                </span>
              ),
            },
            {
              key: "open",
              header: t("portal.review.decide"),
              cell: (row) => (
                <Button variant="secondary" aria-label={t("portal.review.openEntry", { machine: code(row.item.assetId) })} onClick={() => { setOpen(row); }}>
                  {t("portal.review.open")}
                </Button>
              ),
            },
          ]}
        />
      )}
      <ReviewDrawer row={open} onClose={() => { setOpen(null); }} />
      {rows.length === queueSize && <p className="text-sm text-text-muted">{t("portal.review.firstPage", { count: queueSize })}</p>}
    </div>
  );
}

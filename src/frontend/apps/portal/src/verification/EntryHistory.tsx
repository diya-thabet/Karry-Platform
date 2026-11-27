import { useInfiniteQuery, useMutation, useQuery } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useState } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, DataTable, EmptyState, ErrorState, PageHeader, SelectField, Sheet, StatusChip, TextField, useToast } from "@karry/ui";
import { Trail } from "../shell/Trail";
import { useAccess, useApi } from "../app/api";
import { useSiteNames } from "../assets/api";
import { localDay } from "../audit/api";
import { saveFile } from "../imports/api";
import { EntryEvidence } from "./EntryEvidence";
import { EntryPhoto } from "./EntryPhoto";
import { EntriesReadPermission, flagKey, useMachineCodes, type ShiftEntry } from "./queue";

const pageSize = 50;
/** Excel reads the accents of a UTF-8 CSV only with a byte order mark. */
const bom = String.fromCharCode(0xfeff);
/** The most an export takes (pages of 200): a period that big is a database job, not a download. */
const exportMax = 5_000;
const any = "any";

/** The last 30 days, today included: what the history opens with. */
function recentDays(now = new Date()): { from: string; to: string } {
  return { from: localDay(new Date(now.getFullYear(), now.getMonth(), now.getDate() - 29)), to: localDay(now) };
}

/** A CSV cell: quoted when it holds a separator, a quote or a line break. */
function cell(value: string): string {
  return /[";\n\r]/.test(value) ? `"${value.replace(/"/g, "\"\"")}"` : value;
}

/**
 * The shift entries of a period, with the meter photos of each start and end (owner's review 2026-11-27: the current
 * photos and the older ones), for a site or one machine. An entry opens in a panel with its photos large and its
 * versions; the export takes exactly the listed period and filters, never the company's whole history.
 */
export function EntryHistory({ assetId }: { assetId?: string }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const toast = useToast();
  const { sites, name: siteName } = useSiteNames();
  const code = useMachineCodes();
  const [period, setPeriod] = useState(recentDays);
  const [siteId, setSiteId] = useState("");
  const [open, setOpen] = useState<ShiftEntry | null>(null);
  const invalid = period.from !== "" && period.to !== "" && period.to < period.from;
  const locale = intlLocale(i18n.language);
  const number = (value: number | string | null) => (value === null ? "—" : new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(Number(value)));
  const shift = (entry: ShiftEntry) => (entry.shift
    ? t("portal.review.shift", { date: new Date(`${entry.shift.date}T12:00:00Z`), code: entry.shift.code })
    : new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(entry.startedAt)));

  // Who drove: the people the reader may see (an operator they may not see shows as a dash).
  const people = useQuery({
    queryKey: ["identity", "users", "names"],
    queryFn: async () => (await api.GET("/api/v1/identity/users", { params: { query: { limit: 200 } } })).data?.items ?? [],
    enabled: can("identity.users.read"),
    staleTime: 5 * 60_000,
  });
  const person = (id: string) => {
    const found = people.data?.find((user) => user.id === id);
    return found ? `${found.firstName} ${found.lastName}` : "—";
  };

  const query = {
    ...(assetId ? { assetId } : {}),
    ...(siteId ? { siteId } : {}),
    ...(period.from ? { from: period.from } : {}),
    ...(period.to ? { to: period.to } : {}),
  };
  const entries = useInfiniteQuery({
    queryKey: ["fieldops", "entries", "history", query],
    queryFn: async ({ pageParam }) => (await api.GET("/api/v1/fieldops/entries", {
      params: { query: { ...query, limit: pageSize, ...(pageParam ? { cursor: pageParam } : {}) } },
    })).data ?? { items: [], nextCursor: null },
    initialPageParam: "",
    getNextPageParam: (page) => page.nextCursor ?? undefined,
    enabled: can(EntriesReadPermission) && !invalid,
  });
  const rows = entries.data?.pages.flatMap((page) => page.items) ?? [];

  const exported = useMutation({
    mutationFn: async () => {
      const all: ShiftEntry[] = [];
      let cursor: string | undefined;
      do {
        const page = (await api.GET("/api/v1/fieldops/entries", { params: { query: { ...query, limit: 200, ...(cursor ? { cursor } : {}) } } })).data;
        all.push(...(page?.items ?? []));
        cursor = page?.nextCursor ?? undefined;
      } while (cursor && all.length < exportMax);
      return all;
    },
    onSuccess: (all) => {
      const header = [t("portal.review.machine"), t("portal.review.site"), t("roles.operator"), t("portal.review.startPhoto"), t("portal.review.endPhoto"),
        t("portal.review.detail.delta"), t("portal.history.status"), t("portal.review.flags")];
      const lines = all.map((entry) => [
        code(entry.assetId), siteName(entry.siteId), person(entry.operatorId),
        `${String(entry.startValue)} ${entry.unit}`, entry.endValue === null ? "" : `${String(entry.endValue)} ${entry.unit}`,
        entry.usage === null ? "" : `${String(entry.usage)} ${entry.unit}`,
        t(entry.status === "closed" ? "portal.board.state.ended" : "portal.board.state.running"),
        entry.flags.map((flag) => { const key = flagKey(flag); return key ? t(key) : flag; }).join(" | "),
      ].map(cell).join(";"));
      // A BOM, so Excel reads the accents; the period is in the file's name.
      saveFile(new Blob([`${bom}${[header.map(cell).join(";"), ...lines].join("\r\n")}\r\n`], { type: "text/csv;charset=utf-8" }),
        `karry-shifts-${period.from || "start"}-${period.to || "today"}.csv`);
      toast({ status: "ok", title: t("portal.history.exported", { count: all.length }) });
    },
  });

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-end gap-3">
        {!assetId && (
          <SelectField label={t("portal.review.site")} value={siteId || any} onValueChange={(value) => { setSiteId(value === any ? "" : value); }}
            options={[{ value: any, label: t("portal.review.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]} />
        )}
        <TextField label={t("portal.audit.from")} type="date" value={period.from} max={period.to || undefined}
          onChange={(event) => { setPeriod({ ...period, from: event.target.value }); }} />
        <TextField label={t("portal.audit.to")} type="date" value={period.to} min={period.from || undefined}
          error={invalid ? t("portal.audit.rangeInvalid") : undefined}
          onChange={(event) => { setPeriod({ ...period, to: event.target.value }); }} />
        <Button variant="secondary" className="ms-auto" disabled={invalid || rows.length === 0} loading={exported.isPending} onClick={() => { exported.mutate(); }}>
          {t("portal.history.export")}
        </Button>
      </div>

      {entries.isError ? <ErrorState onRetry={() => void entries.refetch()} /> : (
        <DataTable<ShiftEntry>
          caption={t("portal.history.title")}
          rows={rows}
          rowKey={(entry) => entry.id}
          onSelect={setOpen}
          loading={entries.isPending && entries.fetchStatus !== "idle"}
          empty={<EmptyState title={t("portal.history.empty")} />}
          columns={[
            {
              key: "machine", header: t("portal.review.machine"),
              cell: (entry) => (
                <span className="flex flex-col gap-1">
                  <span className="font-semibold">{code(entry.assetId)}</span>
                  <span className="text-sm text-text-muted">{shift(entry)}</span>
                  {!assetId && <span className="text-sm text-text-muted">{siteName(entry.siteId)}</span>}
                </span>
              ),
            },
            { key: "operator", header: t("roles.operator"), cell: (entry) => person(entry.operatorId) },
            {
              key: "meter", header: t("portal.review.meter"),
              cell: (entry) => (
                <span className="flex flex-col gap-1 tabular-nums">
                  <span>{t("portal.review.values", { start: number(entry.startValue), end: number(entry.endValue), unit: entry.unit })}</span>
                  {entry.usage !== null && <span className="font-semibold">{t("portal.review.delta", { delta: number(entry.usage), unit: entry.unit })}</span>}
                </span>
              ),
            },
            {
              key: "photos", header: t("portal.review.photos"),
              cell: (entry) => (
                <span className="flex min-w-max gap-2">
                  <EntryPhoto entryId={entry.id} sha256={entry.startPhoto} label={t("portal.review.startPhoto")} />
                  {entry.endPhoto && <EntryPhoto entryId={entry.id} sha256={entry.endPhoto} label={t("portal.review.endPhoto")} />}
                </span>
              ),
            },
            {
              key: "status", header: t("portal.history.status"),
              cell: (entry) => (
                <span className="flex max-w-60 flex-col items-start gap-1">
                  <StatusChip status={entry.status === "closed" ? "ok" : "pending"}>
                    {t(entry.status === "closed" ? "portal.board.state.ended" : "portal.board.state.running")}
                  </StatusChip>
                  {entry.flags.map((flag) => { const key = flagKey(flag); return <StatusChip key={flag} status="warning" wrap>{key ? t(key) : flag}</StatusChip>; })}
                </span>
              ),
            },
          ]}
        />
      )}
      {entries.hasNextPage && (
        <Button variant="secondary" className="self-center" loading={entries.isFetchingNextPage} onClick={() => void entries.fetchNextPage()}>{t("portal.audit.more")}</Button>
      )}
      {open && <EntrySheet entry={open} onClose={() => { setOpen(null); }} title={`${code(open.assetId)} · ${shift(open)}`} site={siteName(open.siteId)} />}
    </div>
  );
}

/** The history of every site the reader may see (Contrôle › Historique des saisies). */
export function EntryHistoryPage() {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-4">
      <PageHeader
        breadcrumbs={<Trail steps={[{ to: "/review", label: t("portal.nav.review") }]} />}
        title={t("portal.history.title")}
        description={t("portal.history.description")}
        actions={(
          <span className="flex flex-wrap gap-2">
            <Button variant="secondary" asChild><Link to="/review">{t("portal.review.title")}</Link></Button>
            <Button variant="secondary" asChild><Link to="/review/board">{t("portal.board.title")}</Link></Button>
          </span>
        )}
      />
      <EntryHistory />
    </div>
  );
}

/** One entry of the history, to look at: its photos large, the previous end on its meter, its signature and versions. */
function EntrySheet({ entry, title, site, onClose }: { entry: ShiftEntry; title: string; site: string; onClose: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const detail = useQuery({
    queryKey: ["fieldops", "entries", entry.id, "detail"],
    queryFn: async () => (await api.GET("/api/v1/fieldops/entries/{entryId}", { params: { path: { entryId: entry.id } } })).data,
  });
  return (
    <Sheet open onOpenChange={(next) => { if (!next) onClose(); }} title={title} description={site} closeLabel={t("common.close")}>
      <EntryEvidence entry={detail.data?.entry ?? entry} previous={detail.data?.previousOnMeter ?? null} versions={detail.data?.versions ?? []} flags={entry.flags} />
    </Sheet>
  );
}

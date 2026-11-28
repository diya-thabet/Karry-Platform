import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Link } from "@tanstack/react-router";
import { useEffect, useRef, useState } from "react";
import { ApiError } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Checkbox, DataTable, Dialog, EmptyState, ErrorState, PageHeader, SelectField, StatusChip, useToast } from "@karry/ui";
import { useAccess, useApi } from "../app/api";
import { handlesVersionConflict } from "../app/conflicts";
import { useSiteNames } from "../assets/api";
import { ItemsReviewPermission } from "./decisions";
import { EntryPhoto } from "./EntryPhoto";
import { ReviewDrawer } from "./ReviewDrawer";
import { entryFlags, flagKey, queueKey, queueSize, useFieldAccountWarning, useMachineCodes, useReviewQueue, type QueueFilters, type QueueRow } from "./queue";
import { moved, shortcutOf } from "./shortcuts";

const any = "any";
const shortcuts = [["J", "next"], ["K", "previous"], ["Enter", "open"], ["A", "approve"], ["R", "reject"], ["C", "correct"], ["?", "help"]] as const;

/**
 * The controller's queue (US-P2-VER-01, 07; ADR-0038): the entries of the person's sites waiting for a decision,
 * overdue first, then flagged, then the oldest; each with its machine and shift, its meter values and Δ, its photos
 * and its flags in words. It refreshes itself every 5 s while the page is open. J/K move, Enter opens, A approves,
 * R and C open the rejection or the correction, ? lists the keys; entries without a flag can be approved together;
 * on a tablet, a swipe toward the end approves an entry without a flag and toward the start opens it.
 */
export function QueuePage() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const toast = useToast();
  const queryClient = useQueryClient();
  const { sites, name } = useSiteNames();
  const code = useMachineCodes();
  const [filters, setFilters] = useState<QueueFilters>({ siteId: "", flag: "", overdue: false });
  const queue = useReviewQueue(filters);
  const [open, setOpen] = useState<{ row: QueueRow; initial: "reject" | "correct" | null } | null>(null);
  const [focus, setFocus] = useState(0);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [help, setHelp] = useState(false);
  const moving = useRef(false);
  const rows = queue.data ?? [];
  const focusedRow = rows[Math.min(focus, rows.length - 1)];
  const locale = intlLocale(i18n.language);
  const at = (value: string) => new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  // Decimals come as JSON numbers or strings (exact values, ADR-0034): formatted for the reader, one decimal.
  const number = (value: number | string) => new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(Number(value));
  const canReview = can(ItemsReviewPermission);
  const unlinked = useFieldAccountWarning(canReview);
  const pending = (row: QueueRow) => canReview && row.item.status === "pending";
  const unflagged = (row: QueueRow) => pending(row) && row.item.flags.length === 0;
  const chosen = rows.filter((row) => selected.has(row.item.id) && unflagged(row));

  const refresh = () => queryClient.invalidateQueries({ queryKey: queueKey });
  const changedMeanwhile = async (error: unknown) => {
    if (error instanceof ApiError && error.status === 409) {
      toast({ status: "warning", title: t("portal.review.detail.changed") });
      await refresh();
    }
  };
  const approve = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (row: QueueRow) => (await api.POST("/api/v1/verification/items/{itemId}/approve", {
      params: { path: { itemId: row.item.id } }, headers: { "If-Match": row.item.version },
    })).data,
    onSuccess: async () => { toast({ status: "ok", title: t("portal.review.detail.approved") }); await refresh(); },
    onError: changedMeanwhile,
  });
  // Bulk approval: all or nothing, entries without a flag only (US-P2-VER-02 AC1, one ledger entry for the batch).
  const approveMany = useMutation({
    meta: handlesVersionConflict,
    mutationFn: async (many: QueueRow[]) => (await api.POST("/api/v1/verification/items/approve", {
      body: { items: many.map((row) => ({ id: row.item.id, version: row.item.version })) },
    })).data,
    onSuccess: async (result) => {
      toast({ status: "ok", title: t("portal.review.approvedMany", { count: result?.items.length ?? 0 }) });
      setSelected(new Set());
      await refresh();
    },
    onError: changedMeanwhile,
  });

  // The keys (US-P2-VER-07 AC1): never while typing or with a dialog or the panel open (TC-P2-VER-011).
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      const shortcut = shortcutOf(event, document);
      if (!shortcut) {
        return;
      }

      event.preventDefault();
      if (shortcut === "help") {
        setHelp(true);
      } else if (shortcut === "next" || shortcut === "previous") {
        moving.current = true;
        setFocus((index) => Math.max(moved(Math.min(index, rows.length - 1), rows.length, shortcut), 0));
      } else if (focusedRow && pending(focusedRow)) {
        if (shortcut === "approve") {
          approve.mutate(focusedRow);
        } else {
          setOpen({ row: focusedRow, initial: shortcut });
        }
      }
    };
    window.addEventListener("keydown", onKey);
    return () => { window.removeEventListener("keydown", onKey); };
  });

  // The browser's focus follows J and K, so Enter opens the row and a screen reader reads it.
  useEffect(() => {
    if (moving.current) {
      moving.current = false;
      document.querySelector<HTMLElement>("tr[aria-selected=true]")?.focus();
    }
  }, [focus]);

  const toggle = (row: QueueRow, on: boolean) => {
    const next = new Set(selected);
    if (on) next.add(row.item.id);
    else next.delete(row.item.id);
    setSelected(next);
  };

  return (
    <div className="flex flex-col gap-4">
      <PageHeader title={t("portal.review.title")} description={t("portal.review.description")}
        actions={(
          <span className="flex flex-wrap gap-2">
            <Button variant="secondary" asChild><Link to="/review/board">{t("portal.board.title")}</Link></Button>
            <Button variant="secondary" asChild><Link to="/review/history">{t("portal.history.title")}</Link></Button>
            <Button variant="secondary" onClick={() => { setHelp(true); }}>{t("portal.review.keys.title")}</Button>
          </span>
        )} />
      {unlinked && (
        <div role="status" className="rounded-lg border border-warning bg-warning-soft px-4 py-3 text-sm">
          {t("portal.review.unlinkedFieldAccount")}
        </div>
      )}
      <div className="flex flex-wrap items-end gap-3">
        <SelectField label={t("portal.review.site")} value={filters.siteId || any} onValueChange={(value) => { setFilters({ ...filters, siteId: value === any ? "" : value }); }}
          options={[{ value: any, label: t("portal.review.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]} />
        <SelectField label={t("portal.review.flag")} value={filters.flag || any} onValueChange={(value) => { setFilters({ ...filters, flag: value === any ? "" : value }); }}
          options={[{ value: any, label: t("portal.review.anyFlag") }, ...entryFlags.map((flag) => ({ value: flag, label: t(`portal.review.flag.${flag}`) }))]} />
        <Checkbox label={t("portal.review.overdueOnly")} checked={filters.overdue} onChange={(event) => { setFilters({ ...filters, overdue: event.target.checked }); }} />
      </div>
      {canReview && rows.some(unflagged) && (
        <div className="flex flex-wrap items-center gap-3">
          <Button variant="secondary" onClick={() => { setSelected(new Set(rows.filter(unflagged).map((row) => row.item.id))); }}>{t("portal.review.selectAll")}</Button>
          <Button disabled={chosen.length === 0} loading={approveMany.isPending} onClick={() => { approveMany.mutate(chosen); }}>
            {t("portal.review.approveSelected", { count: chosen.length })}
          </Button>
        </div>
      )}
      {queue.isError ? (
        <ErrorState onRetry={() => void queue.refetch()} />
      ) : (
        <DataTable<QueueRow>
          caption={t("portal.review.title")}
          rows={rows}
          rowKey={(row) => row.item.id}
          selectedKey={focusedRow?.item.id}
          onSelect={(row) => { setFocus(rows.indexOf(row)); setOpen({ row, initial: null }); }}
          onSwipe={(row, direction) => {
            if (direction === "end" && unflagged(row)) approve.mutate(row);
            if (direction === "start") setOpen({ row, initial: null });
          }}
          loading={queue.isPending && queue.fetchStatus !== "idle"}
          empty={<EmptyState title={t("portal.review.empty")} />}
          columns={[
            {
              key: "select",
              header: t("portal.review.selectHeader"),
              cell: (row) => (unflagged(row)
                ? (
                    // The checkbox chooses; the table does not open the row for a click on a control.
                    <Checkbox label={<span className="sr-only">{t("portal.review.select", { machine: code(row.item.assetId) })}</span>}
                      checked={selected.has(row.item.id)} onChange={(event) => { toggle(row, event.target.checked); }} />
                  )
                : null),
            },
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
                    // Never narrower than both photos: an automatic table may give the column less (they spilled over the flags).
                    <span className="flex min-w-max gap-2">
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
                    <ul className="flex max-w-60 flex-col items-start gap-1">
                      {item.flags.map((flag) => {
                        const key = flagKey(flag);
                        return <li key={flag}><StatusChip status="warning" wrap>{key ? t(key) : flag}</StatusChip></li>;
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
                <Button variant="secondary" aria-label={t("portal.review.openEntry", { machine: code(row.item.assetId) })}
                  onClick={(event) => { event.stopPropagation(); setFocus(rows.indexOf(row)); setOpen({ row, initial: null }); }}>
                  {t("portal.review.open")}
                </Button>
              ),
            },
          ]}
        />
      )}
      <ReviewDrawer row={open?.row ?? null} initial={open?.initial ?? null} onClose={() => { setOpen(null); }} />
      <Dialog open={help} onOpenChange={setHelp} title={t("portal.review.keys.title")} description={t("portal.review.keys.description")} closeLabel={t("common.close")}>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
          {shortcuts.map(([key, action]) => (
            <div key={key} className="contents">
              <dt><kbd className="rounded border border-border bg-surface-muted px-2 py-0.5 font-mono">{key}</kbd></dt>
              <dd>{t(`portal.review.keys.${action}`)}</dd>
            </div>
          ))}
        </dl>
      </Dialog>
      {rows.length === queueSize && <p className="text-sm text-text-muted">{t("portal.review.firstPage", { count: queueSize })}</p>}
    </div>
  );
}

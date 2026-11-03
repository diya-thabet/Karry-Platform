import { useInfiniteQuery, useMutation, useQueryClient } from "@tanstack/react-query";
import { useState, type SyntheticEvent } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Card, DataTable, Dialog, EmptyState, MaskedValue, PageHeader, SelectField, StatusChip, TextField, useToast } from "@karry/ui";
import { useAccess, useApi } from "../app/api";
import { useSiteNames } from "../assets/api";
import { saveFile } from "../imports/api";
import {
  auditKey, auditQuery, AuditReadPermission, AuditVerifyPermission, latestVerificationKey, maskedFieldsOf, noFilters, shownValue, useAuditActors,
  useLatestVerification, type AuditEntry, type AuditFilters,
} from "./api";

/** "Any" in a select: the select needs a value of its own. */
const any = "any";
const pageSize = 50;

/**
 * The company's audit trail (US-P1-AUD-04, ADR-0029 §2): who changed what, when and on which site, newest first, with
 * the before/after of each change and a CSV export; and the integrity check of the trail (US-P1-AUD-03 AC2). The API
 * decides what the person sees: their sites only, sensitive fields masked.
 */
export function AuditPage() {
  const { t } = useTranslation();
  const { loaded, can } = useAccess();
  const reads = can(AuditReadPermission);
  const verifies = can(AuditVerifyPermission);

  if (loaded && !reads && !verifies) {
    return (
      <div className="flex flex-col gap-4">
        <PageHeader title={t("portal.nav.audit")} />
        <EmptyState title={t("portal.audit.noAccess")}>{t("portal.audit.noAccessHint")}</EmptyState>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader title={t("portal.nav.audit")} description={t("portal.audit.description")} />
      {verifies && <IntegrityCheck />}
      {reads && <AuditLog />}
    </div>
  );
}

/** The last check of the trail's hash chain and anchors, and a button to run one now. */
function IntegrityCheck() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const queryClient = useQueryClient();
  const latest = useLatestVerification();
  const check = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/audit/verify")).data,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: latestVerificationKey }),
  });
  const report = latest.data?.verification;
  const by = latest.data?.verifiedByName ?? latest.data?.verifiedBy;
  const at = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));

  return (
    <Card
      title={t("portal.audit.integrity.title")}
      description={t("portal.audit.integrity.description")}
      actions={<Button variant="secondary" loading={check.isPending} onClick={() => { check.mutate(); }}>{t("portal.audit.integrity.run")}</Button>}
    >
      <div role="status" className="flex flex-col gap-2 text-sm">
        {latest.isPending && <p>{t("common.loading")}</p>}
        {!latest.isPending && !report && <p>{t("portal.audit.integrity.never")}</p>}
        {report && (
          <>
            <div>
              {report.valid
                ? <StatusChip status="ok">{t("portal.audit.integrity.intact")}</StatusChip>
                : <StatusChip status="error">{t("portal.audit.integrity.broken")}</StatusChip>}
            </div>
            <p>{t("portal.audit.integrity.checked", { entries: Number(report.entriesChecked), anchors: Number(report.anchorsChecked ?? 0) })}</p>
            {!report.valid && (
              <p className="font-medium text-danger">
                {t("portal.audit.integrity.firstBroken", { sequence: Number(report.firstBrokenSequence ?? 0) })}
                {report.problem && <> <span translate="no">{report.problem}</span></>}
              </p>
            )}
            <p className="text-text-muted">
              {by ? t("portal.audit.integrity.when", { at: at(report.verifiedAt), by }) : t("portal.audit.integrity.whenNoOne", { at: at(report.verifiedAt) })}
            </p>
          </>
        )}
      </div>
    </Card>
  );
}

/** The entries with their filters, page after page, the export and the detail of one entry. */
function AuditLog() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const { sites, name } = useSiteNames();
  const actors = useAuditActors();
  const [draft, setDraft] = useState<AuditFilters>(noFilters);
  const [filters, setFilters] = useState<AuditFilters>(noFilters);
  const [shown, setShown] = useState<AuditEntry | null>(null);
  const set = (key: keyof AuditFilters, value: string) => { setDraft((current) => ({ ...current, [key]: value })); };
  const invalidRange = draft.fromDay !== "" && draft.toDay !== "" && draft.toDay < draft.fromDay;

  const entries = useInfiniteQuery({
    queryKey: [...auditKey, "entries", filters],
    queryFn: async ({ pageParam }) => (await api.GET("/api/v1/audit/entries", {
      params: { query: { ...auditQuery(filters), limit: pageSize, ...(pageParam ? { cursor: pageParam } : {}) } },
    })).data ?? { entries: [], nextCursor: null },
    initialPageParam: "",
    getNextPageParam: (page) => page.nextCursor ?? undefined,
  });
  const rows = entries.data?.pages.flatMap((page) => page.entries) ?? [];

  const exported = useMutation({
    mutationFn: async () => (await api.GET("/api/v1/audit/entries/export.csv", { params: { query: auditQuery(filters) }, parseAs: "blob" })).data,
    onSuccess: (blob) => {
      if (blob) {
        saveFile(blob, `karry-audit-${new Date().toISOString().slice(0, 10)}.csv`);
        toast({ status: "ok", title: t("portal.audit.exported") });
      }
    },
  });

  const apply = (event: SyntheticEvent) => {
    event.preventDefault();
    if (!invalidRange) {
      setFilters({ ...draft, entityType: draft.entityType.trim(), entityId: draft.entityId.trim(), eventType: draft.eventType.trim() });
    }
  };

  const clear = () => {
    setDraft(noFilters);
    setFilters(noFilters);
  };

  const person = (entry: { actor: string | null; actorName: string | null }) => entry.actorName ?? entry.actor ?? t("portal.audit.system");
  const at = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "short", timeStyle: "medium" }).format(new Date(value));

  return (
    <section aria-labelledby="audit-log" className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <h2 id="audit-log" className="text-lg font-semibold">{t("portal.audit.log")}</h2>
        <Button variant="secondary" loading={exported.isPending} disabled={rows.length === 0} onClick={() => { exported.mutate(); }}>
          {t("portal.audit.export")}
        </Button>
      </div>
      <p className="text-sm text-text-muted">{t("portal.audit.exportHint")}</p>

      <form aria-label={t("portal.audit.filters")} className="flex flex-col gap-4" onSubmit={apply}>
        <div className="grid gap-4 md:grid-cols-3">
          <SelectField
            label={t("portal.audit.person")}
            value={draft.actor || any}
            onValueChange={(value) => { set("actor", value === any ? "" : value); }}
            options={[
              { value: any, label: t("portal.audit.anyPerson") },
              ...(actors.data ?? []).map((actor) => ({ value: actor.actor, label: actor.name ?? actor.actor })),
            ]}
          />
          <SelectField
            label={t("portal.audit.site")}
            value={draft.siteId || any}
            onValueChange={(value) => { set("siteId", value === any ? "" : value); }}
            options={[{ value: any, label: t("portal.audit.anySite") }, ...sites.map((site) => ({ value: site.id, label: site.name }))]}
          />
          <TextField label={t("portal.audit.eventType")} description={t("portal.audit.eventTypeHint")} value={draft.eventType} onChange={(e) => { set("eventType", e.target.value); }} />
          <TextField label={t("portal.audit.entityType")} description={t("portal.audit.entityTypeHint")} value={draft.entityType} onChange={(e) => { set("entityType", e.target.value); }} />
          <TextField label={t("portal.audit.entityId")} value={draft.entityId} onChange={(e) => { set("entityId", e.target.value); }} />
          <div className="grid grid-cols-2 gap-2">
            <TextField label={t("portal.audit.from")} type="date" value={draft.fromDay} onChange={(e) => { set("fromDay", e.target.value); }} />
            <TextField
              label={t("portal.audit.to")}
              type="date"
              value={draft.toDay}
              error={invalidRange ? t("portal.audit.rangeInvalid") : undefined}
              onChange={(e) => { set("toDay", e.target.value); }}
            />
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <Button type="submit">{t("portal.audit.apply")}</Button>
          <Button type="button" variant="ghost" onClick={clear}>{t("portal.audit.clear")}</Button>
        </div>
      </form>

      <DataTable<AuditEntry>
        caption={t("portal.audit.log")}
        rows={rows}
        rowKey={(entry) => String(entry.sequence)}
        loading={entries.isPending}
        empty={<EmptyState title={t("portal.audit.none")} />}
        columns={[
          { key: "at", header: t("portal.audit.when"), cell: (entry) => <time dateTime={entry.occurredAt} className="whitespace-nowrap">{at(entry.occurredAt)}</time> },
          { key: "who", header: t("portal.audit.person"), cell: (entry) => <Identifier value={person(entry)} /> },
          { key: "what", header: t("portal.audit.eventType"), cell: (entry) => <code translate="no" className="text-xs">{entry.eventType}</code> },
          {
            key: "on",
            header: t("portal.audit.subject"),
            cell: (entry) => entry.entityType ? <span translate="no" className="whitespace-nowrap">{entry.entityType} · <Identifier value={entry.entityId ?? ""} /></span> : "—",
          },
          { key: "site", header: t("portal.audit.site"), cell: (entry) => entry.siteId ? <span translate="no">{name(entry.siteId)}</span> : t("portal.audit.companyWide") },
          {
            key: "detail",
            header: <span className="sr-only">{t("portal.audit.details")}</span>,
            cell: (entry) => (
              <Button variant="ghost" aria-label={t("portal.audit.detailsOf", { sequence: Number(entry.sequence) })} onClick={() => { setShown(entry); }}>
                {t("portal.audit.details")}
              </Button>
            ),
          },
        ]}
      />
      {entries.hasNextPage && (
        <div>
          <Button variant="secondary" loading={entries.isFetchingNextPage} onClick={() => void entries.fetchNextPage()}>{t("portal.audit.more")}</Button>
        </div>
      )}
      <EntryDialog entry={shown} person={shown ? person(shown) : ""} at={shown ? at(shown.occurredAt) : ""} onClose={() => { setShown(null); }} />
    </section>
  );
}

/** One entry: what changed (before → after), the fields the policy hides, and the whole event as recorded. */
function EntryDialog({ entry, person, at, onClose }: { entry: AuditEntry | null; person: string; at: string; onClose: () => void }) {
  const { t } = useTranslation();
  const masked = entry ? maskedFieldsOf(entry) : [];

  return (
    <Dialog
      open={entry !== null}
      onOpenChange={(open) => { if (!open) onClose(); }}
      title={t("portal.audit.entryTitle", { sequence: Number(entry?.sequence ?? 0) })}
      description={entry ? t("portal.audit.entryDescription", { person, at }) : ""}
      closeLabel={t("common.close")}
    >
      {entry && (
        <div className="flex max-h-[60vh] flex-col gap-4 overflow-y-auto text-sm">
          <p><code translate="no">{entry.eventType}</code></p>
          {entry.changes.length > 0
            ? (
              <table className="w-full text-start">
                <caption className="sr-only">{t("portal.audit.changes")}</caption>
                <thead>
                  <tr className="text-text-muted">
                    <th scope="col" className="py-1 pe-2 font-medium">{t("portal.audit.field")}</th>
                    <th scope="col" className="py-1 pe-2 font-medium">{t("portal.audit.before")}</th>
                    <th scope="col" className="py-1 font-medium">{t("portal.audit.after")}</th>
                  </tr>
                </thead>
                <tbody>
                  {entry.changes.map((change) => (
                    <tr key={change.path} className="border-t border-divider align-top">
                      <th scope="row" className="py-1 pe-2 font-normal"><code translate="no">{change.path}</code></th>
                      <td className="break-all py-1 pe-2" translate="no">{shownValue(change.before)}</td>
                      <td className="break-all py-1" translate="no">{shownValue(change.after)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )
            : <p className="text-text-muted">{t("portal.audit.noChanges")}</p>}
          {masked.length > 0 && (
            <div className="flex flex-col gap-1">
              <p>{t("portal.audit.maskedFields")}</p>
              <ul className="flex flex-col gap-1">
                {masked.map((field) => (
                  <li key={field} className="flex items-center gap-2">
                    <code translate="no">{field}</code>
                    <MaskedValue label={t("portal.audit.masked")} />
                  </li>
                ))}
              </ul>
            </div>
          )}
          {entry.event
            ? (
              <details>
                <summary className="cursor-pointer">{t("portal.audit.recorded")}</summary>
                <pre translate="no" className="mt-2 overflow-x-auto rounded-lg bg-surface-muted p-3 text-xs">{JSON.stringify(entry.event, null, 2)}</pre>
              </details>
            )
            : <p className="text-text-muted">{t("portal.audit.unknownEvent")}</p>}
        </div>
      )}
    </Dialog>
  );
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * An identifier as people can scan it in a list (UX-10): a UUID shortened to its first eight characters, the whole
 * value in the tooltip and for screen readers; anything else as it is.
 */
function Identifier({ value }: { value: string }) {
  if (!uuid.test(value)) {
    return <span translate="no">{value}</span>;
  }

  return (
    <span translate="no" title={value} className="font-mono text-xs">
      <span aria-hidden>{`${value.slice(0, 8)}…`}</span>
      <span className="sr-only">{value}</span>
    </span>
  );
}

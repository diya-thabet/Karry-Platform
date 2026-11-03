import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useState, type ChangeEvent } from "react";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, buttonVariants, DataTable, EmptyState, PageHeader, PageSkeleton, SelectField, StatusChip, useToast } from "@karry/ui";
import { useAccess, useApi } from "../app/api";
import { Trail } from "../shell/Trail";
import {
  importKey, importKinds, importsKey, isRunning, saveFile, shownLimit, workbookType,
  type ImportIssue, type ImportJob, type ImportPlannedRow,
} from "./api";

/**
 * Loading a company's data from Excel (US-P1-ORG-03, ADR-0027): pick what to load, fill in the template, upload it,
 * read the report (every problem on its cell), check the preview and commit. All or nothing per file; the same file
 * twice changes nothing. Only the kinds the person may change are offered (the API checks every call anyway).
 */
export function ImportsPage() {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const kinds = importKinds.filter((k) => can(k.permission));
  const [kind, setKind] = useState<string>(kinds[0]?.kind ?? "sites");
  const [language, setLanguage] = useState(i18n.language === "en" ? "en" : "fr");
  const [jobId, setJobId] = useState<string | undefined>();
  const queryClient = useQueryClient();
  const recent = useQuery({
    queryKey: importsKey,
    queryFn: async () => (await api.GET("/api/v1/imports", { params: { query: { limit: 10 } } })).data?.items ?? [],
  });

  const template = useMutation({
    mutationFn: async () => (await api.GET("/api/v1/imports/templates/{kind}", { params: { path: { kind }, query: { language } }, parseAs: "blob" })).data,
    onSuccess: (blob) => { if (blob) saveFile(blob, `karry-${kind}-${language}.xlsx`); },
  });

  const upload = useMutation({
    mutationFn: async (file: File) => (await api.POST("/api/v1/imports", {
      params: { query: { kind, fileName: file.name } },
      body: file as unknown as string,
      bodySerializer: (body: unknown) => body as BodyInit,
      headers: { "Content-Type": workbookType },
    })).data,
    onSuccess: async (job) => {
      if (job) {
        queryClient.setQueryData(importKey(job.id), job);
        setJobId(job.id);
      }

      await queryClient.invalidateQueries({ queryKey: importsKey, exact: true });
    },
  });

  const onFile = (event: ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    if (file) {
      upload.mutate(file);
    }

    event.target.value = "";
  };

  if (kinds.length === 0) {
    return <EmptyState title={t("portal.imports.nothing")} />;
  }

  return (
    <div className="flex flex-col gap-6">
      <PageHeader breadcrumbs={<Trail steps={[{ to: "/settings", label: t("portal.nav.settings") }]} />} title={t("portal.imports.title")} />

      {jobId
        ? <ImportJobView key={jobId} jobId={jobId} onDone={() => { setJobId(undefined); }} />
        : (
          <section aria-labelledby="import-start" className="flex flex-col gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
            <h2 id="import-start" className="text-base font-semibold">{t("portal.imports.start")}</h2>
            <p className="text-text-muted">{t("portal.imports.orderHint")}</p>
            <div className="flex flex-wrap items-end gap-3">
              <SelectField
                label={t("portal.imports.kind")}
                options={kinds.map((k) => ({ value: k.kind, label: t(`portal.imports.kind.${k.kind}`) }))}
                value={kind}
                onValueChange={setKind}
              />
              <SelectField
                label={t("portal.imports.language")}
                options={[{ value: "fr", label: t("common.language.fr") }, { value: "en", label: t("common.language.en") }]}
                value={language}
                onValueChange={setLanguage}
              />
              <Button type="button" variant="secondary" loading={template.isPending} onClick={() => { template.mutate(); }}>
                {t("portal.imports.template")}
              </Button>
            </div>
            <p className="text-sm text-text-muted">{t("portal.imports.templateHint")}</p>
            <div className="flex flex-wrap items-center gap-3">
              <label className={`${buttonVariants()} cursor-pointer focus-within:outline focus-within:outline-2`}>
                {upload.isPending ? t("portal.imports.uploading") : t("portal.imports.upload")}
                <input type="file" accept={`.xlsx,${workbookType}`} className="sr-only" disabled={upload.isPending} onChange={onFile} />
              </label>
              <span className="text-sm text-text-muted">{t("portal.imports.uploadHint")}</span>
            </div>
          </section>
        )}

      <section aria-labelledby="import-recent" className="flex flex-col gap-3">
        <h2 id="import-recent" className="text-base font-semibold">{t("portal.imports.recent")}</h2>
        <DataTable<ImportJob>
          caption={t("portal.imports.recent")}
          rows={recent.data ?? []}
          rowKey={(job) => job.id}
          selectedKey={jobId}
          onSelect={(job) => { setJobId(job.id); }}
          loading={recent.isPending}
          empty={<EmptyState title={t("portal.imports.none")} />}
          columns={[
            { key: "file", header: t("portal.imports.file"), cell: (job) => job.fileName },
            { key: "kind", header: t("portal.imports.kind"), cell: (job) => t(`portal.imports.kind.${job.kind}`, { defaultValue: job.kind }) },
            { key: "status", header: t("portal.imports.status"), cell: (job) => <JobStatus job={job} /> },
            { key: "at", header: t("portal.imports.uploadedAt"), cell: (job) => new Date(job.uploadedAt).toLocaleString(intlLocale(i18n.language)) },
          ]}
        />
      </section>
    </div>
  );
}

function JobStatus({ job }: { job: ImportJob }) {
  const { t } = useTranslation();
  const chip = job.status === "committed" ? "ok"
    : job.status === "discarded" ? "pending"
      : isRunning(job) ? "pending"
        : Number(job.errors) > 0 ? "error" : "warning";
  const status = job.status === "validated" && Number(job.errors) > 0 ? "hasErrors" : job.status;
  return <StatusChip status={chip}>{t(`portal.imports.status.${status}`, { defaultValue: job.status })}</StatusChip>;
}

/** One import: waits for the worker, then shows the report, the preview and what the person can do next. */
function ImportJobView({ jobId, onDone }: { jobId: string; onDone: () => void }) {
  const { t } = useTranslation();
  const api = useApi();
  const toast = useToast();
  const queryClient = useQueryClient();
  const job = useQuery({
    queryKey: importKey(jobId),
    queryFn: async () => (await api.GET("/api/v1/imports/{importId}", { params: { path: { importId: jobId } } })).data,
    refetchInterval: (query) => (isRunning(query.state.data) ? 1000 : false),
  });
  const current = job.data;
  const validated = current?.status === "validated";
  const status = current?.status;
  // The recent imports show the state the worker reached.
  useEffect(() => {
    if (status) void queryClient.invalidateQueries({ queryKey: importsKey, exact: true });
  }, [status, queryClient]);
  // The report and the preview belong to one validation: they are read again whenever its time changes.
  const version = current?.changedAt;
  const issues = useQuery({
    queryKey: [...importKey(jobId), "issues", version],
    queryFn: async () => (await api.GET("/api/v1/imports/{importId}/issues", { params: { path: { importId: jobId }, query: { limit: shownLimit } } })).data,
    enabled: validated,
  });
  const preview = useQuery({
    queryKey: [...importKey(jobId), "rows", version],
    queryFn: async () => (await api.GET("/api/v1/imports/{importId}/rows", { params: { path: { importId: jobId }, query: { limit: shownLimit } } })).data,
    enabled: validated && Number(current.errors) === 0,
  });

  const changed = async (next: ImportJob | undefined) => {
    if (next) queryClient.setQueryData(importKey(jobId), next);
    await queryClient.invalidateQueries({ queryKey: importsKey, exact: true });
  };
  const commit = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/imports/{importId}/commit", { params: { path: { importId: jobId } } })).data,
    onSuccess: changed,
  });
  const discard = useMutation({
    mutationFn: async () => (await api.POST("/api/v1/imports/{importId}/discard", { params: { path: { importId: jobId } } })).data,
    onSuccess: async (next) => {
      await changed(next);
      toast({ status: "ok", title: t("portal.imports.discarded") });
      onDone();
    },
  });

  if (!current) {
    return <PageSkeleton />;
  }

  const kind = t(`portal.imports.kind.${current.kind}`, { defaultValue: current.kind });
  const errors = Number(current.errors);
  return (
    <section aria-labelledby="import-job" className="flex flex-col gap-4 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-5 shadow-[var(--karry-card-shadow)]">
      <div className="flex flex-wrap items-center gap-3">
        <h2 id="import-job" className="text-base font-semibold">{`${current.fileName} · ${kind}`}</h2>
        <JobStatus job={current} />
      </div>

      {isRunning(current) && (
        <p role="status">{current.status === "validating" ? t("portal.imports.validating") : t("portal.imports.committing")}</p>
      )}

      {current.status === "committed" && (
        <p role="status" className="font-semibold">
          {t("portal.imports.committed", { created: Number(current.created), updated: Number(current.updated), unchanged: Number(current.unchanged) })}
        </p>
      )}

      {current.status === "discarded" && <p role="status">{t("portal.imports.discardedHint")}</p>}

      {validated && (
        <>
          <p role="status">
            {errors > 0
              ? t("portal.imports.blocked", { rows: Number(current.rows), errors })
              : t("portal.imports.ready", { rows: Number(current.rows) })}
          </p>
          {(errors > 0 || Number(current.warnings) > 0) && <IssueList issues={issues.data?.items ?? []} more={Boolean(issues.data?.nextCursor)} loading={issues.isPending} />}
          {errors === 0 && <PreviewList rows={preview.data?.items ?? []} more={Boolean(preview.data?.nextCursor)} loading={preview.isPending} />}
          <div className="flex flex-wrap gap-3">
            {errors === 0 && (
              <Button type="button" loading={commit.isPending} onClick={() => { commit.mutate(); }}>{t("portal.imports.commit")}</Button>
            )}
            <Button type="button" variant="secondary" loading={discard.isPending} onClick={() => { discard.mutate(); }}>{t("portal.imports.discard")}</Button>
          </div>
          {errors > 0 && <p className="text-sm text-text-muted">{t("portal.imports.fixHint")}</p>}
        </>
      )}

      {!isRunning(current) && !validated && (
        <div>
          <Button type="button" variant="secondary" onClick={onDone}>{t("portal.imports.another")}</Button>
        </div>
      )}
    </section>
  );
}

/** The report (US-P1-ORG-03 AC2): each problem with its row and cell, in the person's language. */
function IssueList({ issues, more, loading }: { issues: ImportIssue[]; more: boolean; loading: boolean }) {
  const { t } = useTranslation();
  return (
    <div className="flex flex-col gap-2">
      <DataTable<ImportIssue>
        caption={t("portal.imports.issues")}
        rows={issues}
        rowKey={(issue) => `${String(issue.row)}-${issue.cell ?? issue.column ?? ""}-${issue.code}`}
        loading={loading}
        empty={<EmptyState title={t("common.empty.title")} />}
        columns={[
          { key: "row", header: t("portal.imports.row"), cell: (issue) => (Number(issue.row) === 0 ? t("portal.imports.wholeFile") : String(issue.row)) },
          { key: "cell", header: t("portal.imports.cell"), cell: (issue) => issue.cell ?? "—" },
          {
            key: "severity", header: t("portal.imports.severity"),
            cell: (issue) => <StatusChip status={issue.severity === "error" ? "error" : "warning"}>{t(`portal.imports.severity.${issue.severity}`, { defaultValue: issue.severity })}</StatusChip>,
          },
          { key: "problem", header: t("portal.imports.problem"), cell: (issue) => <IssueText issue={issue} /> },
        ]}
      />
      {more && <p className="text-sm text-text-muted">{t("portal.imports.moreIssues", { count: shownLimit })}</p>}
    </div>
  );
}

/**
 * A problem in the person's words: the import's own codes and every module code the portal knows (the same words as
 * on the screens); any other shows its code, so support can still tell what it is.
 */
function IssueText({ issue }: { issue: ImportIssue }) {
  const { t, i18n } = useTranslation();
  const key = `errors.${issue.code}`;
  return <>{i18n.exists(key) ? t(key, { ...issue.args }) : t("portal.imports.otherProblem", { code: issue.code })}</>;
}

/** The preview (US-P1-ORG-03 AC3): what the commit would do with each row. */
function PreviewList({ rows, more, loading }: { rows: ImportPlannedRow[]; more: boolean; loading: boolean }) {
  const { t } = useTranslation();
  const count = (action: string) => rows.filter((row) => row.action === action).length;
  return (
    <div className="flex flex-col gap-2">
      <DataTable<ImportPlannedRow>
        caption={t("portal.imports.preview")}
        rows={rows}
        rowKey={(row) => String(row.row)}
        loading={loading}
        empty={<EmptyState title={t("common.empty.title")} />}
        columns={[
          { key: "row", header: t("portal.imports.row"), cell: (row) => String(row.row) },
          { key: "key", header: t("portal.imports.key"), cell: (row) => row.key },
          { key: "action", header: t("portal.imports.action"), cell: (row) => t(`portal.imports.action.${row.action}`, { defaultValue: row.action }) },
        ]}
      />
      {more
        ? <p className="text-sm text-text-muted">{t("portal.imports.moreRows", { count: shownLimit })}</p>
        : !loading && <p className="font-semibold">{t("portal.imports.plan", { created: count("create"), updated: count("update"), unchanged: count("unchanged") })}</p>}
    </div>
  );
}

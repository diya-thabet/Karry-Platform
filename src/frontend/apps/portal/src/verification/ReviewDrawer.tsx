import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { ApiError } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Dialog, SelectField, Sheet, StatusChip, TextAreaField, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { useSiteNames } from "../assets/api";
import { handlesVersionConflict } from "../app/conflicts";
import { SignatureVerification } from "../audit/SignatureVerification";
import { EntryPhoto } from "./EntryPhoto";
import { correctedFields, explanationOk, ItemsReviewPermission, rejectionReasons, SecondApprovePermission } from "./decisions";
import { EntriesReadPermission, flagKey, queueKey, useMachineCodes, type QueueRow, type ShiftEntry } from "./queue";
import { ShiftReportButton } from "./ShiftReportButton";

/**
 * One entry as a controller decides on it (US-P2-VER-02 … 04, ADR-0038 §4): the previous end on the meter beside this
 * start and end, the values and Δ, the flags in words, the signature's verification, the versions; then approve,
 * reject with a reason, correct (before → after, with a reason) or give the second approval. A decision the server
 * refuses for a change meanwhile (409) closes the panel and the queue shows the entry as it now is.
 */
export function ReviewDrawer({ row, initial = null, onClose }: { row: QueueRow | null; initial?: "reject" | "correct" | null; onClose: () => void }) {
  return row ? <ReviewDetail key={row.item.id} row={row} initial={initial} onClose={onClose} /> : null;
}

function ReviewDetail({ row, initial, onClose }: { row: QueueRow; initial: "reject" | "correct" | null; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const toast = useToast();
  const queryClient = useQueryClient();
  const withStepUp = useWithStepUp();
  const { item } = row;
  const code = useMachineCodes();
  const { name: siteName } = useSiteNames();
  // R and C in the queue open the panel straight on the rejection or the correction (ADR-0038 §4).
  const [dialog, setDialog] = useState<"reject" | "correct" | null>(initial);
  const locale = intlLocale(i18n.language);
  const number = (value: number | string | null | undefined) =>
    (value === null || value === undefined ? "—" : new Intl.NumberFormat(locale, { maximumFractionDigits: 1 }).format(Number(value)));

  const detail = useQuery({
    queryKey: ["fieldops", "entries", item.recordId, "detail"],
    queryFn: async () => (await api.GET("/api/v1/fieldops/entries/{entryId}", { params: { path: { entryId: item.recordId } } })).data,
    enabled: can(EntriesReadPermission) && item.recordType === "fieldops.shift_entry",
  });
  const entry = detail.data?.entry ?? row.entry;
  const previous = detail.data?.previousOnMeter ?? null;

  const done = async (message: string) => {
    toast({ status: "ok", title: message });
    await queryClient.invalidateQueries({ queryKey: queueKey });
    onClose();
  };
  const conflict = async (error: unknown) => {
    if (error instanceof ApiError && error.status === 409) {
      toast({ status: "warning", title: t("portal.review.detail.changed") });
      await queryClient.invalidateQueries({ queryKey: queueKey });
      onClose();
    }
  };
  const decision = { meta: handlesVersionConflict, onError: conflict };
  const ifMatch = { "If-Match": item.version };

  const approve = useMutation({
    ...decision,
    mutationFn: async () => (await api.POST("/api/v1/verification/items/{itemId}/approve", { params: { path: { itemId: item.id } }, headers: ifMatch })).data,
    onSuccess: () => done(t("portal.review.detail.approved")),
  });
  const secondApprove = useMutation({
    ...decision,
    mutationFn: () => withStepUp(async () => (await api.POST("/api/v1/verification/items/{itemId}/second-approve", {
      params: { path: { itemId: item.id } }, headers: ifMatch,
    })).data),
    onSuccess: () => done(t("portal.review.detail.approved")),
  });

  const pending = item.status === "pending";
  const canReview = can(ItemsReviewPermission) && pending;
  const canSecond = can(SecondApprovePermission) && item.status === "awaiting_second";
  // Which entry, under the panel's title: the machine, its shift and the site.
  const which = [
    code(item.assetId),
    ...(entry?.shift ? [t("portal.review.shift", { date: new Date(`${entry.shift.date}T12:00:00Z`), code: entry.shift.code })] : []),
    siteName(item.siteId),
  ].join(" · ");
  const actions = canReview || canSecond || Boolean(entry?.shift);

  return (
    <Sheet open onOpenChange={(open) => { if (!open) onClose(); }} title={t("portal.review.detail.title")} description={which} closeLabel={t("common.close")}
      aside={(
        <>
          {item.escalatedAt && <StatusChip status="error">{t("portal.review.overdue")}</StatusChip>}
          {item.status === "awaiting_second" && <StatusChip status="warning">{t("portal.review.awaitingSecond")}</StatusChip>}
        </>
      )}
      footer={actions
        ? (
            <>
              {canReview && <Button loading={approve.isPending} onClick={() => { approve.mutate(); }}>{t("portal.review.detail.approve")}</Button>}
              {canReview && <Button variant="secondary" onClick={() => { setDialog("reject"); }}>{t("portal.review.detail.reject")}</Button>}
              {canReview && entry && <Button variant="secondary" onClick={() => { setDialog("correct"); }}>{t("portal.review.detail.correct")}</Button>}
              {canSecond && <Button loading={secondApprove.isPending} onClick={() => { secondApprove.mutate(); }}>{t("portal.review.detail.secondApprove")}</Button>}
              {entry?.shift && <span className="ms-auto"><ShiftReportButton siteId={entry.siteId} date={entry.shift.date} shift={entry.shift.code} /></span>}
            </>
          )
        : undefined}>
      <div className="flex flex-col gap-6">
        {item.flags.length > 0 && (
          <section aria-label={t("portal.review.flags")} className="flex flex-wrap gap-2">
            {item.flags.map((flag) => { const key = flagKey(flag); return <StatusChip key={flag} status="warning">{key ? t(key) : flag}</StatusChip>; })}
          </section>
        )}

        {/* The previous end on the meter beside this start and end: a gap shows at a glance (ADR-0038 §4). */}
        <section aria-label={t("portal.review.photos")} className="grid grid-cols-1 gap-4 md:grid-cols-3">
          <Figure label={t("portal.review.detail.previousEnd")} value={previous ? `${number(previous.endValue)} ${previous.unit}` : null}>
            {previous?.endPhoto
              ? <EntryPhoto entryId={previous.id} sha256={previous.endPhoto} label={t("portal.review.detail.previousEnd")} size={220} />
              : <NoPhoto text={t("portal.review.detail.noPrevious")} />}
          </Figure>
          <Figure label={t("portal.review.startPhoto")} value={entry ? `${number(entry.startValue)} ${entry.unit}` : null}>
            {entry && <EntryPhoto entryId={entry.id} sha256={entry.startPhoto} label={t("portal.review.startPhoto")} size={220} />}
          </Figure>
          <Figure label={t("portal.review.endPhoto")} value={entry ? `${number(entry.endValue)} ${entry.unit}` : null}>
            {entry?.endPhoto ? <EntryPhoto entryId={entry.id} sha256={entry.endPhoto} label={t("portal.review.endPhoto")} size={220} /> : <NoPhoto text="—" />}
          </Figure>
        </section>

        {entry && (
          <dl className="grid grid-cols-2 gap-3">
            <Fact label={t("portal.review.detail.delta")} value={entry.usage === null ? "—" : `${number(entry.usage)} ${entry.unit}`} />
            <Fact label={t("portal.review.detail.fuel")} value={t("portal.review.detail.fuelValues", { start: entry.fuelStartPercent, end: entry.fuelEndPercent ?? "—" })} />
          </dl>
        )}

        {entry && <SignatureVerification envelopeId={entry.envelopeId} />}

        {detail.data && detail.data.versions.length > 1 && (
          <section aria-label={t("portal.review.detail.versions")} className="flex flex-col gap-2">
            <h3 className="font-semibold">{t("portal.review.detail.versions")}</h3>
            <ol className="flex flex-col divide-y divide-divider rounded-lg border border-divider text-sm">
              {detail.data.versions.map((version) => (
                <li key={version.version} className="px-3 py-2">
                  {t("portal.review.detail.version", { version: version.version, start: number(version.startValue), end: number(version.endValue), unit: version.unit })}
                </li>
              ))}
            </ol>
          </section>
        )}
      </div>

      <RejectDialog open={dialog === "reject"} onClose={() => { setDialog(null); }} itemId={item.id} version={item.version} decision={decision} done={done} />
      {entry && <CorrectDialog open={dialog === "correct"} onClose={() => { setDialog(null); }} itemId={item.id} version={item.version} entry={entry} decision={decision} done={done} />}
    </Sheet>
  );
}

/** A meter photo with what it shows, the value large enough to compare across the three. */
function Figure({ label, value, children }: { label: string; value: string | null; children: ReactNode }) {
  return (
    <figure className="flex flex-col gap-2 rounded-xl border border-divider bg-surface-muted p-3">
      <figcaption className="text-sm font-medium text-text-muted">{label}</figcaption>
      {children}
      {value !== null && <span className="text-lg font-semibold tabular-nums">{value}</span>}
    </figure>
  );
}

function NoPhoto({ text }: { text: string }) {
  return <span className="flex aspect-square w-full max-w-[220px] items-center justify-center rounded-md border border-dashed border-border p-3 text-center text-sm text-text-muted">{text}</span>;
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-xl border border-divider px-4 py-3">
      <dt className="text-sm text-text-muted">{label}</dt>
      <dd className="text-lg font-semibold tabular-nums">{value}</dd>
    </div>
  );
}

interface DialogProps {
  open: boolean;
  onClose: () => void;
  itemId: string;
  version: string;
  decision: { meta: typeof handlesVersionConflict; onError: (error: unknown) => Promise<void> };
  done: (message: string) => Promise<void>;
}

function RejectDialog({ open, onClose, itemId, version, decision, done }: DialogProps) {
  const { t } = useTranslation();
  const api = useApi();
  const [reason, setReason] = useState<string>(rejectionReasons[0]);
  const [comment, setComment] = useState("");
  const reject = useMutation({
    ...decision,
    mutationFn: async () => (await api.POST("/api/v1/verification/items/{itemId}/reject", {
      params: { path: { itemId } }, headers: { "If-Match": version }, body: { reasonCode: reason, comment: comment.trim() },
    })).data,
    onSuccess: () => done(t("portal.review.detail.rejected")),
  });
  const ok = explanationOk(comment);

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next) onClose(); }} title={t("portal.review.detail.reject")} description={t("portal.review.detail.rejectHint")}
      closeLabel={t("common.cancel")}
      footer={<Button disabled={!ok} loading={reject.isPending} onClick={() => { reject.mutate(); }}>{t("portal.review.detail.reject")}</Button>}>
      <div className="flex flex-col gap-3">
        <SelectField label={t("portal.review.detail.reason")} value={reason} onValueChange={setReason}
          options={rejectionReasons.map((code) => ({ value: code, label: t(`portal.review.reason.${code}`) }))} />
        <TextAreaField label={t("portal.review.detail.comment")} value={comment} onChange={(event) => { setComment(event.target.value); }}
          error={comment.length > 0 && !ok ? t("portal.review.detail.explanationRule") : undefined} />
      </div>
    </Dialog>
  );
}

function CorrectDialog({ open, onClose, itemId, version, entry, decision, done }: DialogProps & { entry: ShiftEntry }) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const text = (value: number | string | null) => (value === null ? "" : String(value));
  const [typed, setTyped] = useState({
    startValue: text(entry.startValue), endValue: text(entry.endValue), fuelStartPercent: text(entry.fuelStartPercent), fuelEndPercent: text(entry.fuelEndPercent),
  });
  const [reason, setReason] = useState("");
  const fields = correctedFields(entry, typed);
  const ok = fields !== null && Object.keys(fields).length > 0 && explanationOk(reason);
  const correct = useMutation({
    ...decision,
    mutationFn: () => withStepUp(async () => (await api.POST("/api/v1/verification/items/{itemId}/correct", {
      params: { path: { itemId } }, headers: { "If-Match": version }, body: { fields: fields ?? {}, reason: reason.trim() },
    })).data),
    onSuccess: (result) => done(result?.next.status === "awaiting_second" ? t("portal.review.detail.correctedWaiting") : t("portal.review.detail.corrected")),
  });
  const field = (name: keyof typeof typed, label: string) => (
    <TextField label={label} inputMode="decimal" value={typed[name]} onChange={(event) => { setTyped({ ...typed, [name]: event.target.value }); }} />
  );

  return (
    <Dialog open={open} onOpenChange={(next) => { if (!next) onClose(); }} title={t("portal.review.detail.correct")} description={t("portal.review.detail.correctHint")}
      closeLabel={t("common.cancel")}
      footer={<Button disabled={!ok} loading={correct.isPending} onClick={() => { correct.mutate(); }}>{t("portal.review.detail.correct")}</Button>}>
      <div className="grid grid-cols-2 gap-3">
        {field("startValue", t("portal.review.detail.startValue", { unit: entry.unit }))}
        {field("endValue", t("portal.review.detail.endValue", { unit: entry.unit }))}
        {field("fuelStartPercent", t("portal.review.detail.fuelStart"))}
        {field("fuelEndPercent", t("portal.review.detail.fuelEnd"))}
        <div className="col-span-2">
          <TextAreaField label={t("portal.review.detail.correctionReason")} value={reason} onChange={(event) => { setReason(event.target.value); }}
            error={reason.length > 0 && !explanationOk(reason) ? t("portal.review.detail.explanationRule") : undefined} />
        </div>
        {fields === null && <p role="alert" className="col-span-2 text-sm text-danger">{t("portal.review.detail.valueRule")}</p>}
      </div>
    </Dialog>
  );
}

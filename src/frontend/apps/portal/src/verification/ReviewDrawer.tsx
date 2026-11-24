import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { ApiError } from "@karry/api-client";
import { intlLocale, useTranslation } from "@karry/i18n";
import { Button, Dialog, Drawer, SelectField, StatusChip, TextAreaField, TextField, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { handlesVersionConflict } from "../app/conflicts";
import { SignatureVerification } from "../audit/SignatureVerification";
import { EntryPhoto } from "./EntryPhoto";
import { correctedFields, explanationOk, ItemsReviewPermission, rejectionReasons, SecondApprovePermission } from "./decisions";
import { EntriesReadPermission, flagKey, queueKey, type QueueRow, type ShiftEntry } from "./queue";

/**
 * One entry as a controller decides on it (US-P2-VER-02 … 04, ADR-0038 §4): the previous end on the meter beside this
 * start and end, the values and Δ, the flags in words, the signature's verification, the versions; then approve,
 * reject with a reason, correct (before → after, with a reason) or give the second approval. A decision the server
 * refuses for a change meanwhile (409) closes the panel and the queue shows the entry as it now is.
 */
export function ReviewDrawer({ row, onClose }: { row: QueueRow | null; onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Drawer open={row !== null} onOpenChange={(open) => { if (!open) onClose(); }} title={t("portal.review.detail.title")} closeLabel={t("common.close")}>
      {row && <ReviewDetail key={row.item.id} row={row} onClose={onClose} />}
    </Drawer>
  );
}

function ReviewDetail({ row, onClose }: { row: QueueRow; onClose: () => void }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const toast = useToast();
  const queryClient = useQueryClient();
  const withStepUp = useWithStepUp();
  const { item } = row;
  const [dialog, setDialog] = useState<"reject" | "correct" | null>(null);
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

  return (
    <div className="flex flex-col gap-5">
      <section aria-label={t("portal.review.photos")} className="grid grid-cols-1 gap-3 sm:grid-cols-3">
        <Figure label={t("portal.review.detail.previousEnd")}>
          {previous?.endPhoto
            ? <EntryPhoto entryId={previous.id} sha256={previous.endPhoto} label={t("portal.review.detail.previousEnd")} size={160} />
            : <span className="text-sm text-text-muted">{t("portal.review.detail.noPrevious")}</span>}
          {previous && <span className="text-sm">{number(previous.endValue)} {previous.unit}</span>}
        </Figure>
        <Figure label={t("portal.review.startPhoto")}>
          {entry && <EntryPhoto entryId={entry.id} sha256={entry.startPhoto} label={t("portal.review.startPhoto")} size={160} />}
          {entry && <span className="text-sm">{number(entry.startValue)} {entry.unit}</span>}
        </Figure>
        <Figure label={t("portal.review.endPhoto")}>
          {entry?.endPhoto && <EntryPhoto entryId={entry.id} sha256={entry.endPhoto} label={t("portal.review.endPhoto")} size={160} />}
          {entry && <span className="text-sm">{number(entry.endValue)} {entry.unit}</span>}
        </Figure>
      </section>

      {entry && (
        <dl className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
          <dt className="text-text-muted">{t("portal.review.detail.delta")}</dt>
          <dd className="font-semibold">{entry.usage === null ? "—" : `${number(entry.usage)} ${entry.unit}`}</dd>
          <dt className="text-text-muted">{t("portal.review.detail.fuel")}</dt>
          <dd>{t("portal.review.detail.fuelValues", { start: entry.fuelStartPercent, end: entry.fuelEndPercent ?? "—" })}</dd>
        </dl>
      )}

      <section aria-label={t("portal.review.flags")} className="flex flex-col gap-1">
        <h3 className="font-semibold">{t("portal.review.flags")}</h3>
        {item.flags.length === 0
          ? <p className="text-sm text-text-muted">{t("portal.review.noFlags")}</p>
          : <ul className="list-disc ps-5 text-sm">{item.flags.map((flag) => { const key = flagKey(flag); return <li key={flag}>{key ? t(key) : flag}</li>; })}</ul>}
      </section>

      {entry && <SignatureVerification envelopeId={entry.envelopeId} />}

      {detail.data && detail.data.versions.length > 1 && (
        <section aria-label={t("portal.review.detail.versions")} className="flex flex-col gap-1">
          <h3 className="font-semibold">{t("portal.review.detail.versions")}</h3>
          <ol className="text-sm">
            {detail.data.versions.map((version) => (
              <li key={version.version}>
                {t("portal.review.detail.version", { version: version.version, start: number(version.startValue), end: number(version.endValue), unit: version.unit })}
              </li>
            ))}
          </ol>
        </section>
      )}

      {item.status === "awaiting_second" && <StatusChip status="warning">{t("portal.review.awaitingSecond")}</StatusChip>}

      <div className="flex flex-wrap gap-2">
        {canReview && <Button loading={approve.isPending} onClick={() => { approve.mutate(); }}>{t("portal.review.detail.approve")}</Button>}
        {canReview && <Button variant="secondary" onClick={() => { setDialog("reject"); }}>{t("portal.review.detail.reject")}</Button>}
        {canReview && entry && <Button variant="secondary" onClick={() => { setDialog("correct"); }}>{t("portal.review.detail.correct")}</Button>}
        {canSecond && <Button loading={secondApprove.isPending} onClick={() => { secondApprove.mutate(); }}>{t("portal.review.detail.secondApprove")}</Button>}
      </div>

      <RejectDialog open={dialog === "reject"} onClose={() => { setDialog(null); }} itemId={item.id} version={item.version} decision={decision} done={done} />
      {entry && <CorrectDialog open={dialog === "correct"} onClose={() => { setDialog(null); }} itemId={item.id} version={item.version} entry={entry} decision={decision} done={done} />}
    </div>
  );
}

function Figure({ label, children }: { label: string; children: ReactNode }) {
  return (
    <figure className="flex flex-col items-start gap-1">
      <figcaption className="text-sm font-semibold">{label}</figcaption>
      {children}
    </figure>
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

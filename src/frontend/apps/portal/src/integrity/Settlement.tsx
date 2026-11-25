import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useState } from "react";
import { ApiError } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, Dialog, SelectField, TextAreaField, useToast } from "@karry/ui";
import { useAccess, useApi, useWithStepUp } from "../app/api";
import { handlesVersionConflict } from "../app/conflicts";
import { flagsKey, FlagsSettlePermission, settlementCommentOk, settlementReasons, type IntegrityFlag } from "./api";

/**
 * Settling a continuity gap (US-P2-FO-08, ADR-0037 §7): one person proposes a reason and what happened, another
 * approves or declines; proposing and approving ask for a recent sign-in. A change meanwhile (409) refreshes the list.
 */
export function Settlement({ flag }: { flag: IntegrityFlag }) {
  const { t } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const toast = useToast();
  const queryClient = useQueryClient();
  const withStepUp = useWithStepUp();
  const [proposing, setProposing] = useState(false);
  const settle = can(FlagsSettlePermission);

  const after = async (message: string) => {
    toast({ status: "ok", title: message });
    await queryClient.invalidateQueries({ queryKey: flagsKey });
  };
  const decision = {
    meta: handlesVersionConflict,
    onError: async (error: unknown) => {
      if (error instanceof ApiError && error.status === 409) {
        toast({ status: "warning", title: t("portal.integrity.settle.changed") });
        await queryClient.invalidateQueries({ queryKey: flagsKey });
      }
    },
  };
  const ifMatch = { "If-Match": flag.version ?? "" };
  const approve = useMutation({
    ...decision,
    mutationFn: () => withStepUp(async () => (await api.POST("/api/v1/integrity/flags/{flagId}/settlement/approve", {
      params: { path: { flagId: flag.id } }, headers: ifMatch,
    })).data),
    onSuccess: () => after(t("portal.integrity.settle.approved")),
  });
  const decline = useMutation({
    ...decision,
    mutationFn: async () => (await api.POST("/api/v1/integrity/flags/{flagId}/settlement/decline", {
      params: { path: { flagId: flag.id } }, headers: ifMatch, body: { comment: null },
    })).data,
    onSuccess: () => after(t("portal.integrity.settle.declined")),
  });

  if (flag.rule !== "RULE-FO-01" || flag.closedAt !== null) {
    return null;
  }

  const pending = flag.settlement;
  return (
    <span className="flex flex-col items-start gap-2">
      {pending && (
        <span className="text-sm">
          {t("portal.integrity.settle.pending", { reason: t(`portal.integrity.settle.reason.${pending.reason}`, { defaultValue: pending.reason }), comment: pending.comment })}
        </span>
      )}
      {settle && !pending && <Button variant="secondary" onClick={() => { setProposing(true); }}>{t("portal.integrity.settle.action")}</Button>}
      {settle && pending && (
        <span className="flex flex-wrap gap-2">
          <Button loading={approve.isPending} onClick={() => { approve.mutate(); }}>{t("portal.integrity.settle.approve")}</Button>
          <Button variant="secondary" loading={decline.isPending} onClick={() => { decline.mutate(); }}>{t("portal.integrity.settle.decline")}</Button>
        </span>
      )}
      {proposing && <ProposeDialog flag={flag} onClose={() => { setProposing(false); }} decision={decision} after={after} />}
    </span>
  );
}

function ProposeDialog({ flag, onClose, decision, after }: {
  flag: IntegrityFlag;
  onClose: () => void;
  decision: { meta: typeof handlesVersionConflict; onError: (error: unknown) => Promise<void> };
  after: (message: string) => Promise<void>;
}) {
  const { t } = useTranslation();
  const api = useApi();
  const withStepUp = useWithStepUp();
  const [reason, setReason] = useState<string>(settlementReasons[0]);
  const [comment, setComment] = useState("");
  const propose = useMutation({
    ...decision,
    mutationFn: () => withStepUp(async () => (await api.POST("/api/v1/integrity/flags/{flagId}/settle", {
      params: { path: { flagId: flag.id } }, headers: { "If-Match": flag.version ?? "" }, body: { reason, comment: comment.trim() },
    })).data),
    onSuccess: async () => { onClose(); await after(t("portal.integrity.settle.proposed")); },
  });
  const ok = settlementCommentOk(comment);

  return (
    <Dialog open onOpenChange={(open) => { if (!open) onClose(); }} title={t("portal.integrity.settle.title")} description={t("portal.integrity.settle.hint")}
      closeLabel={t("common.cancel")}
      footer={<Button disabled={!ok} loading={propose.isPending} onClick={() => { propose.mutate(); }}>{t("portal.integrity.settle.propose")}</Button>}>
      <div className="flex flex-col gap-3">
        <SelectField label={t("portal.integrity.settle.reasonLabel")} value={reason} onValueChange={setReason}
          options={settlementReasons.map((code) => ({ value: code, label: t(`portal.integrity.settle.reason.${code}`) }))} />
        {reason === "unexplained" && <p role="note" className="text-sm text-danger">{t("portal.integrity.settle.unexplainedWarning")}</p>}
        <TextAreaField label={t("portal.integrity.settle.comment")} value={comment} onChange={(event) => { setComment(event.target.value); }} />
      </div>
    </Dialog>
  );
}

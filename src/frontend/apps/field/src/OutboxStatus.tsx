import { useEffect, useState } from "react";
import { useTranslation } from "@karry/i18n";
import { watchOutbox, type KarryOfflineDb, type OutboxSummary } from "@karry/offline";
import { StatusChip } from "@karry/ui";

/** Codes the operator gets an explanation for; any other is shown with its code. */
const reasons: Record<string, string> = {
  "sync.blob_mismatch": "field.outbox.reason.blobMismatch",
  "sync.evidence_invalid": "field.outbox.reason.evidenceInvalid",
  "sync.signature_invalid": "field.outbox.reason.signatureInvalid",
  "sync.sequence_reused": "field.outbox.reason.sequenceReused",
};

/**
 * What the tablet still has to send (US-P2-OFF-02, sync centre): "everything is sent", or how many entries wait to be
 * sent or for an earlier one, and the refused ones with the reason for each. It follows the outbox as it changes.
 */
export function OutboxStatus({ db }: { db: KarryOfflineDb }) {
  const { t } = useTranslation();
  const [summary, setSummary] = useState<OutboxSummary | null>(null);

  useEffect(() => watchOutbox(db, setSummary), [db]);

  if (!summary) {
    return null;
  }

  const unsent = summary.pending + summary.waiting;
  const refused = summary.rejected.length;
  return (
    <section aria-label={t("field.outbox.label")} className="flex flex-col gap-2 rounded-xl border-[length:var(--karry-border-width)] border-divider bg-surface p-4">
      <div className="flex flex-wrap items-center gap-3">
        <StatusChip status={refused > 0 ? "error" : unsent > 0 ? "pending" : "ok"}>{t("field.sync.pending", { count: summary.pending })}</StatusChip>
        {summary.waiting > 0 && <p>{t("field.outbox.waiting", { count: summary.waiting })}</p>}
      </div>
      {refused > 0 && (
        <details className="flex flex-col gap-2">
          <summary className="cursor-pointer font-medium text-danger">{t("field.outbox.rejected", { count: refused })}</summary>
          <ul className="flex flex-col gap-2 ps-4">
            {summary.rejected.map((command) => (
              <li key={command.commandId}>
                <p className="text-text-muted">{t("field.outbox.recordedAt", { at: new Date(command.deviceTime) })}</p>
                <p>{command.code && command.code in reasons ? t(reasons[command.code] ?? "") : t("field.outbox.reason.other", { code: command.code ?? "?" })}</p>
              </li>
            ))}
          </ul>
        </details>
      )}
    </section>
  );
}

import { intlLocale, useTranslation } from "@karry/i18n";
import { StatusChip, type Status } from "@karry/ui";
import { useAccess } from "../app/api";
import { AuditVerifyPermission, useEnvelopeVerification } from "./api";

const statusChip: Record<string, Status> = { verified: "ok", pending: "pending", invalid: "error" };
const resultChip: Record<string, Status> = { pass: "ok", fail: "error", pending: "pending", not_checked: "pending" };
// The checks this version knows, in the order the server sends them; a newer check is shown by its name.
const knownChecks = new Set(["envelope", "device_signature", "server_countersignature", "ledger_entry", "record_payload"]);

/**
 * A signature's "Verified ✔" badge and what is behind it (US-P2-SIG-04 AC2): each check the server ran with its result,
 * the server key and the time of the countersignature. Shown to people who may verify the trail; nothing otherwise.
 */
export function SignatureVerification({ envelopeId }: { envelopeId: string }) {
  const { t, i18n } = useTranslation();
  const { can } = useAccess();
  const verification = useEnvelopeVerification(envelopeId);
  if (!can(AuditVerifyPermission)) return null;

  const at = (value: string) => new Intl.DateTimeFormat(intlLocale(i18n.language), { dateStyle: "medium", timeStyle: "short" }).format(new Date(value));
  const result = verification.data;

  return (
    <section aria-label={t("portal.signature.title")} className="flex flex-col gap-2 rounded-lg border border-divider p-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium">{t("portal.signature.title")}</span>
        {verification.isPending && <span className="text-text-muted">{t("portal.signature.checking")}</span>}
        {verification.isError && <StatusChip status="warning">{t("portal.signature.unavailable")}</StatusChip>}
        {result && <StatusChip status={statusChip[result.status] ?? "warning"}>{t(`portal.signature.status.${result.status}`, { defaultValue: result.status })}</StatusChip>}
      </div>
      {result && (
        <>
          <ul aria-label={t("portal.signature.checks")} className="flex flex-col gap-1">
            {result.checks.map((check) => (
              <li key={check.name} className="flex flex-wrap items-center justify-between gap-2">
                <span>{knownChecks.has(check.name) ? t(`portal.signature.check.${check.name}`) : <code translate="no">{check.name}</code>}</span>
                <StatusChip status={resultChip[check.result] ?? "warning"}>{t(`portal.signature.result.${check.result}`, { defaultValue: check.result })}</StatusChip>
              </li>
            ))}
          </ul>
          {result.countersignedAt && result.serverKeyId && (
            <p className="text-text-muted">{t("portal.signature.countersigned", { at: at(result.countersignedAt), key: result.serverKeyId })}</p>
          )}
        </>
      )}
    </section>
  );
}

import { useCallback, useMemo, useState, type ReactNode } from "react";
import { ApiError } from "@karry/api-client";
import { useAuth } from "@karry/auth";
import { useTranslation } from "@karry/i18n";
import { Button, Dialog, ErrorState } from "@karry/ui";
import { ReportContext } from "./reportError";

/** Problems the person can act on get their own words; any other shows the generic text and the support reference. */
const explained = new Set([
  "auth.step_up_required",
  "billing.feature_not_in_plan",
  "billing.limit_reached",
  "billing.subscription_restricted",
  "billing.subscription_suspended",
  "billing.subscription_inactive",
  "tenant.host_mismatch",
  "identity.escalation",
  "identity.scope_exceeded",
  "identity.role_in_use",
  "identity.role_exists",
  "identity.cannot_change_own_roles",
  "identity.role_needs_account",
  "identity.role_name_invalid",
]);

/**
 * One place where failed actions surface (US-P1-OPS-01 AC3): a dialog with the support reference, i.e. the
 * correlation ID the API returned. Errors without one (network down) show the same dialog without a reference.
 * An account of another company on this address (`tenant.host_mismatch`) gets a notice and a way to sign out.
 */
export function ErrorDialogProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const { signOut } = useAuth();
  const [error, setError] = useState<unknown>(null);
  const code = error instanceof ApiError && explained.has(error.code) ? error.code : undefined;
  const mismatch = code === "tenant.host_mismatch";
  const report = useCallback((reported: unknown) => { setError(reported); }, []);
  const value = useMemo(() => report, [report]);

  return (
    <ReportContext value={value}>
      {children}
      <Dialog
        open={error !== null}
        onOpenChange={(open) => { if (!open) setError(null); }}
        title={mismatch ? t("errors.tenant.host_mismatch.title") : t("common.error.title")}
        description={code ? t(`errors.${code}`) : t("common.error.body")}
        closeLabel={t("common.close")}
        footer={
          <>
            {mismatch && <Button onClick={() => void signOut()}>{t("common.error.signOut")}</Button>}
            <Button variant="secondary" onClick={() => { setError(null); }}>{t("common.close")}</Button>
          </>
        }
      >
        {!code && <ErrorState correlationId={error instanceof ApiError ? error.correlationId : undefined} />}
      </Dialog>
    </ReportContext>
  );
}

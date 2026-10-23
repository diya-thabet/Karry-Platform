import { useCallback, useMemo, useState, type ReactNode } from "react";
import { ApiError } from "@karry/api-client";
import { useTranslation } from "@karry/i18n";
import { Button, Dialog, ErrorState } from "@karry/ui";
import { ReportContext } from "./reportError";


/**
 * One place where failed actions surface (US-P1-OPS-01 AC3): a dialog with the support reference, i.e. the
 * correlation ID the API returned. Errors without one (network down) show the same dialog without a reference.
 */
export function ErrorDialogProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation();
  const [error, setError] = useState<unknown>(null);
  const report = useCallback((reported: unknown) => { setError(reported); }, []);
  const value = useMemo(() => report, [report]);

  return (
    <ReportContext value={value}>
      {children}
      <Dialog
        open={error !== null}
        onOpenChange={(open) => { if (!open) setError(null); }}
        title={t("common.error.title")}
        description={t("common.error.body")}
        closeLabel={t("common.close")}
        footer={<Button variant="secondary" onClick={() => { setError(null); }}>{t("common.close")}</Button>}
      >
        <ErrorState correlationId={error instanceof ApiError ? error.correlationId : undefined} />
      </Dialog>
    </ReportContext>
  );
}

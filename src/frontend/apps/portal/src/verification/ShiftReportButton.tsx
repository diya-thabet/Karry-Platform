import { useMutation } from "@tanstack/react-query";
import { FileDown } from "lucide-react";
import { useTranslation } from "@karry/i18n";
import { Button, useToast } from "@karry/ui";
import { useAccess, useApi } from "../app/api";
import { saveFile } from "../imports/api";

export const ShiftReportsReadPermission = "documents.shift-reports.read";

/**
 * Issues and downloads a shift's report as a PDF (US-P2-SIG-05, ADR-0040 §3): each click issues a new document with its
 * own QR, in the reader's view and language. Shown only to people who may read shift reports.
 */
export function ShiftReportButton({ siteId, date, shift }: { siteId: string; date: string; shift: string }) {
  const { t, i18n } = useTranslation();
  const api = useApi();
  const { can } = useAccess();
  const toast = useToast();
  const issued = useMutation({
    mutationFn: async () => (await api.GET("/api/v1/documents/shift-reports", {
      params: { query: { siteId, date, shift, language: i18n.language } },
      parseAs: "blob",
    })).data,
    onSuccess: (pdf) => {
      if (pdf) {
        saveFile(pdf, `shift-report-${date}-${shift}.pdf`);
        toast({ status: "ok", title: t("portal.shiftReport.issued") });
      }
    },
  });

  if (!can(ShiftReportsReadPermission)) {
    return null;
  }

  return (
    <Button variant="secondary" loading={issued.isPending} onClick={() => { issued.mutate(); }}>
      <FileDown aria-hidden className="size-4" />
      {t("portal.shiftReport.download")}
    </Button>
  );
}

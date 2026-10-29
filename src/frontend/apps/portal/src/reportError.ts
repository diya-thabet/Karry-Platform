import { createContext, use } from "react";

export const ReportContext = createContext<((error: unknown, reload?: () => void) => void) | null>(null);

/**
 * Shows an error in the portal's error dialog (with the support reference when the API returned one); `reload` loads
 * the data again after the person fixed the cause from the dialog.
 */
export function useReportError(): (error: unknown, reload?: () => void) => void {
  const report = use(ReportContext);
  if (!report) {
    throw new Error("useReportError must be used inside <ErrorDialogProvider>.");
  }

  return report;
}

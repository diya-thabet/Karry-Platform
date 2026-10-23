import { createContext, use } from "react";

export const ReportContext = createContext<((error: unknown) => void) | null>(null);

/** Shows an error in the portal's error dialog (with the support reference when the API returned one). */
export function useReportError(): (error: unknown) => void {
  const report = use(ReportContext);
  if (!report) {
    throw new Error("useReportError must be used inside <ErrorDialogProvider>.");
  }

  return report;
}

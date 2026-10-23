import { QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, type RouterHistory } from "@tanstack/react-router";
import { useState } from "react";
import { I18nProvider, type I18n } from "@karry/i18n";
import { ThemeProvider, ToastProvider } from "@karry/ui";
import { ErrorDialogProvider } from "./ErrorDialog";
import { createQueryClient } from "./queryClient";
import { useReportError } from "./reportError";
import { createPortalRouter } from "./router";

function DataLayer({ history }: { history?: RouterHistory | undefined }) {
  const report = useReportError();
  const [client] = useState(() => createQueryClient(report));
  const [router] = useState(() => createPortalRouter(history));
  return (
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}

export function App({ i18n, history }: { i18n: I18n; history?: RouterHistory }) {
  return (
    <I18nProvider i18n={i18n}>
      <ThemeProvider defaultTheme="light" density="office">
        <ToastProvider label="Notifications">
          <ErrorDialogProvider>
            <DataLayer history={history} />
          </ErrorDialogProvider>
        </ToastProvider>
      </ThemeProvider>
    </I18nProvider>
  );
}

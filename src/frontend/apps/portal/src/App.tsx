import { QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider, type RouterHistory } from "@tanstack/react-router";
import { useState } from "react";
import type { ApiClient } from "@karry/api-client";
import { AuthProvider, useAuth, type Auth } from "@karry/auth";
import { I18nProvider, type I18n } from "@karry/i18n";
import { LanguageDirection, ThemeProvider, ToastProvider } from "@karry/ui";
import { useApi } from "./app/api";
import { ApiProvider } from "./app/ApiProvider";
import { ErrorDialogProvider } from "./app/ErrorDialog";
import { FlagsProvider } from "./app/FlagsProvider";
import { isOperator } from "./operator/api";
import { SupportBanner, SupportVisitProvider } from "./operator/support";
import { supportClient, useSupportVisit, type SupportVisit } from "./operator/supportVisit";
import { createQueryClient } from "./app/queryClient";
import { useReportError } from "./app/reportError";
import { createPortalRouter } from "./router";

function DataLayer({ history, operator, lastCompany }: { history?: RouterHistory | undefined; operator: boolean; lastCompany?: string | undefined }) {
  const report = useReportError();
  const [client] = useState(() => createQueryClient(report));
  const [router] = useState(() => createPortalRouter(history, { operator, lastCompany }));
  return (
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}

/**
 * A company's portal, or the operator console for Karry staff signed in on the platform address (ADR-0029 §5): they
 * have no company, so no company flags are asked for them. During a support visit the operator sees the company's
 * portal, every call naming the session, under the support banner (ADR-0029 §4).
 */
function Session({ history }: { history?: RouterHistory | undefined }) {
  const { user } = useAuth();
  const { visit, lastCompany } = useSupportVisit();
  if (!isOperator(user)) {
    return (
      <FlagsProvider>
        <DataLayer history={history} operator={false} />
      </FlagsProvider>
    );
  }

  return visit ? <SupportedCompany key={visit.sessionId} visit={visit} history={history} /> : <DataLayer history={history} operator lastCompany={lastCompany} />;
}

/** The company's own screens with a fresh cache, so nothing of the console or of another visit shows in them. */
function SupportedCompany({ visit, history }: { visit: SupportVisit; history?: RouterHistory | undefined }) {
  const api = useApi();
  const { leave } = useSupportVisit();
  const [client] = useState(() => supportClient(api, visit, () => { leave("refused"); }));
  return (
    <div className="pb-24 sm:pb-16">
      <ApiProvider client={client}>
        <FlagsProvider>
          <DataLayer history={history} operator={false} />
        </FlagsProvider>
      </ApiProvider>
      <SupportBanner visit={visit} />
    </div>
  );
}

export function App({ i18n, auth, api, history }: { i18n: I18n; auth: Auth; api: ApiClient; history?: RouterHistory }) {
  return (
    <AuthProvider auth={auth}>
    <ApiProvider client={api}>
    <I18nProvider i18n={i18n}>
      <ThemeProvider defaultTheme="light" density="office">
      <LanguageDirection>
        <ToastProvider label="Notifications">
          <ErrorDialogProvider>
            <SupportVisitProvider>
              <Session history={history} />
            </SupportVisitProvider>
          </ErrorDialogProvider>
        </ToastProvider>
      </LanguageDirection>
      </ThemeProvider>
    </I18nProvider>
    </ApiProvider>
    </AuthProvider>
  );
}

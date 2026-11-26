import type { ApiClient } from "@karry/api-client";
import { I18nProvider, type I18n } from "@karry/i18n";
import { LanguageDirection, ThemeProvider } from "@karry/ui";
import { VerifyPage } from "./VerifyPage";
import type { VerifyAddress } from "./verify";

/** The public page on its own: no sign-in, no company, no other screen of the portal (ADR-0040 §4, §5). */
export function VerifyApp({ i18n, api, address }: { i18n: I18n; api: ApiClient; address: VerifyAddress }) {
  return (
    <I18nProvider i18n={i18n}>
      <ThemeProvider defaultTheme="light" density="office">
        <LanguageDirection>
          <VerifyPage api={api} address={address} />
        </LanguageDirection>
      </ThemeProvider>
    </I18nProvider>
  );
}

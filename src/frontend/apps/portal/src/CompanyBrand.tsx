import { useEffect } from "react";
import { isLanguage, useTranslation } from "@karry/i18n";
import { savedLanguage } from "@karry/ui";
import { useCurrentTenant } from "./api";

/**
 * The app's title with the company's logo and name (US-P1-TEN-04); Karry's subtitle until the company is known. The
 * name is company data, never translated (`translate="no"`).
 */
export function CompanyBrand() {
  const { t } = useTranslation();
  const { tenant } = useCurrentTenant();
  return (
    <div className="flex items-center gap-3">
      {tenant?.logoUrl && <img src={tenant.logoUrl} alt="" className="h-10 max-w-32 object-contain" />}
      <div>
        <p className="text-2xl font-bold">{t("portal.title")}</p>
        {tenant ? <p className="text-text-muted" translate="no">{tenant.name}</p> : <p className="text-text-muted">{t("portal.subtitle")}</p>}
      </div>
    </div>
  );
}

/** A thin bar in the company's colour; decorative (the colour carries no meaning). */
export function CompanyStripe() {
  const { tenant } = useCurrentTenant();
  return tenant?.primaryColour ? <div aria-hidden className="h-1" style={{ backgroundColor: tenant.primaryColour }} /> : null;
}

/**
 * The company's default language for people who never chose one on this device (US-P1-OFF-06 AC1: user, then company,
 * then browser, then French). Follows a settings change at once, without signing in again.
 */
export function CompanyLanguage() {
  const { i18n } = useTranslation();
  const { tenant } = useCurrentTenant();
  const language = tenant?.language;
  useEffect(() => {
    if (savedLanguage() === null && isLanguage(language) && i18n.language !== language) {
      void i18n.changeLanguage(language);
      document.documentElement.lang = language;
    }
  }, [i18n, language]);
  return null;
}

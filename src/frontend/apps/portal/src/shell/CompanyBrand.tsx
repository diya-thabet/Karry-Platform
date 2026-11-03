import { useEffect } from "react";
import { isLanguage, useTranslation } from "@karry/i18n";
import { savedLanguage } from "@karry/ui";
import { useCurrentTenant } from "../app/api";

/**
 * The app's title with the company's logo and name (US-P1-TEN-04); Karry's subtitle until the company is known. The
 * name is company data, never translated (`translate="no"`).
 */
export function CompanyBrand() {
  const { t } = useTranslation();
  const { tenant } = useCurrentTenant();
  return (
    <div className="flex min-w-0 items-center gap-3">
      {tenant?.logoUrl
        ? <img src={tenant.logoUrl} alt="" className="size-9 shrink-0 rounded-lg object-contain" />
        : (
          <span aria-hidden className="flex size-9 shrink-0 items-center justify-center rounded-lg bg-primary text-sm font-bold text-primary-text">
            {(tenant?.name ?? "K").slice(0, 1).toUpperCase()}
          </span>
        )}
      <div className="flex min-w-0 flex-col leading-tight">
        {tenant
          ? <p className="truncate text-sm font-semibold text-text" translate="no">{tenant.name}</p>
          : <p className="truncate text-sm font-semibold text-text">{t("common.appName")}</p>}
        <p className="text-xs text-text-muted">{t("portal.title")}</p>
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
      void i18n.changeLanguage(language); // the page's lang and dir follow
    }
  }, [i18n, language]);
  return null;
}

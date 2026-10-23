import i18next, { type i18n } from "i18next";
import ICU from "i18next-icu";
import { initReactI18next } from "react-i18next";
import en from "./catalogs/en.json" with { type: "json" };
import fr from "./catalogs/fr.json" with { type: "json" };
import { type Language } from "./languages";
import { pseudoLocalize } from "./pseudo";

export const catalogs: Record<Language, Record<string, string>> = { fr, en };

export interface I18nOptions {
  language: Language;
  /** Staging build: every string pseudo-localized (VITE_PSEUDO_LOCALE=1). */
  pseudo?: boolean;
}

/**
 * A ready i18next instance with ICU message format (plurals, numbers, dates per locale) and both catalogs
 * bundled, so a language switch is instant and works offline (US-P1-OFF-06 AC1). Keys are flat ("portal.nav.home")
 * and never used as fallback text: a missing key shows the key, and the catalog check fails in CI.
 */
export async function createI18n(options: I18nOptions): Promise<i18n> {
  const instance = i18next.createInstance();
  const resources = Object.fromEntries(
    Object.entries(catalogs).map(([language, messages]) => [
      language,
      { translation: options.pseudo ? Object.fromEntries(Object.entries(messages).map(([k, v]) => [k, pseudoLocalize(v)])) : messages },
    ]),
  );
  await instance
    .use(ICU)
    .use(initReactI18next)
    .init({
      lng: options.language,
      fallbackLng: false,
      resources,
      keySeparator: false,
      nsSeparator: false,
      returnNull: false,
      interpolation: { escapeValue: false }, // React escapes
      react: { useSuspense: false },
    });
  return instance;
}

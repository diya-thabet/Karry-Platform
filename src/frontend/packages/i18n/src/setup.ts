import i18next, { type BackendModule, type i18n } from "i18next";
import ICU from "i18next-icu";
import { initReactI18next } from "react-i18next";
import { applyDocumentLanguage, isLanguage, supportedLanguages, type Language } from "./languages";
import { pseudoLocalize } from "./pseudo";

type Catalog = Record<string, string>;

/**
 * One chunk per language, loaded when the language is first shown: five catalogs bundled together would add about
 * 55 KB (gzip) to every first download. The field app's service worker precaches every chunk, so a switch works offline.
 */
const loaders: Record<Language, () => Promise<{ default: Catalog }>> = {
  fr: () => import("./catalogs/fr.json"),
  en: () => import("./catalogs/en.json"),
  ar: () => import("./catalogs/ar.json"),
  emk: () => import("./catalogs/emk.json"),
  nqo: () => import("./catalogs/nqo.json"),
};

function bundledCatalogs(pseudo: boolean): BackendModule {
  return {
    type: "backend",
    init: () => undefined,
    read: (language, _namespace, callback) => {
      if (!isLanguage(language)) {
        callback(null, {});
        return;
      }

      loaders[language]().then(
        ({ default: messages }) => {
          callback(null, pseudo ? Object.fromEntries(Object.entries(messages).map(([key, value]) => [key, pseudoLocalize(value)])) : messages);
        },
        (error: unknown) => { callback(error instanceof Error ? error : new Error(String(error)), null); },
      );
    },
  };
}

export interface I18nOptions {
  language: Language;
  /** Staging build: every string pseudo-localized (VITE_PSEUDO_LOCALE=1). */
  pseudo?: boolean;
}

/**
 * A ready i18next instance with ICU message format (plurals, numbers, dates per locale), the start language's
 * catalog loaded; another language's catalog loads on the switch, from the app's own files, so it works offline
 * (US-P1-OFF-06 AC1) and `changeLanguage` resolves once the text is there. Keys are flat ("portal.nav.home")
 * and never used as fallback text: a missing key shows the key, and the catalog check fails in CI. The page's
 * `lang` and `dir` follow the language, so a switch to Arabic or N'Ko mirrors the layout at once.
 */
export async function createI18n(options: I18nOptions): Promise<i18n> {
  const instance = i18next.createInstance();
  await instance
    .use(ICU)
    .use(bundledCatalogs(options.pseudo ?? false))
    .use(initReactI18next)
    .init({
      lng: options.language,
      fallbackLng: false,
      supportedLngs: [...supportedLanguages],
      load: "currentOnly",
      keySeparator: false,
      nsSeparator: false,
      returnNull: false,
      interpolation: { escapeValue: false }, // React escapes
      react: { useSuspense: false },
    });
  applyDocumentLanguage(instance.language);
  instance.on("languageChanged", applyDocumentLanguage);
  return instance;
}

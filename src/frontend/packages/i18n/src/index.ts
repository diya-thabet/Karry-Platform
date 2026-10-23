// FR/EN catalogs (ICU), i18next setup, language resolution and pseudo-localization (US-P1-OFF-06).
// Source-first internal package: apps import TypeScript directly, no build step.
export { createI18n, catalogs, type I18nOptions } from "./setup";
export { defaultLanguage, isLanguage, resolveLanguage, supportedLanguages, type Language } from "./languages";
export { pseudoClose, pseudoLocalize, pseudoOpen } from "./pseudo";
export { I18nextProvider as I18nProvider, useTranslation } from "react-i18next";
export type { i18n as I18n } from "i18next";

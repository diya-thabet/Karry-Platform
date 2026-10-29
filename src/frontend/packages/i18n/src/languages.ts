/**
 * Languages of the product, French first (the primary market): French, English, Modern Standard Arabic, and Malinké
 * (Eastern Maninkakan, ISO 639-3 `emk`) in Latin script and in N'Ko script (`nqo`, as browsers and CLDR name it).
 * Arabic, Malinké and N'Ko catalogs are drafts until reviewed by native speakers (docs/04-phases/…/languages).
 */
export const supportedLanguages = ["fr", "en", "ar", "emk", "nqo"] as const;

export type Language = (typeof supportedLanguages)[number];

export const defaultLanguage: Language = "fr";

/** Languages written right to left: the document and the components mirror for them. */
const rightToLeft: readonly string[] = ["ar", "nqo"];

export function languageDirection(language: string): "ltr" | "rtl" {
  return rightToLeft.includes(language) ? "rtl" : "ltr";
}

/**
 * The locale for `Intl` dates and numbers. Browsers have no data for Malinké in Latin script: it would fall back to
 * American English, so it takes the formats of Guinea's administration (French, Guinea). N'Ko has its own (N'Ko digits).
 */
export function intlLocale(language: string): string {
  return language === "emk" ? "fr-GN" : language;
}

/** Sets `lang` and `dir` of the page (assistive technologies, hyphenation, fonts and the mirrored layout follow them). */
export function applyDocumentLanguage(language: string): void {
  if (typeof document === "undefined") {
    return;
  }

  document.documentElement.lang = language;
  document.documentElement.dir = languageDirection(language);
}

export function isLanguage(value: string | null | undefined): value is Language {
  return supportedLanguages.includes(value as Language);
}

/**
 * Which language a user sees (US-P1-OFF-06 AC1): the user's own choice, else the company default, else the first
 * supported browser language, else French. Region and script tags count ("fr-GN" → fr, "ar-MA" → ar).
 */
export function resolveLanguage(options: {
  user?: string | null;
  tenant?: string | null;
  browser?: readonly string[];
}): Language {
  const candidates = [options.user, options.tenant, ...(options.browser ?? [])];
  for (const candidate of candidates) {
    const base = candidate?.toLowerCase().split("-")[0];
    if (isLanguage(base)) {
      return base;
    }
  }

  return defaultLanguage;
}

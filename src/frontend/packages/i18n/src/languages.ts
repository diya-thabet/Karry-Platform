/** Languages of the product (FR + EN from day 1; Arabic RTL later). French first: the primary market. */
export const supportedLanguages = ["fr", "en"] as const;

export type Language = (typeof supportedLanguages)[number];

export const defaultLanguage: Language = "fr";

export function isLanguage(value: string | null | undefined): value is Language {
  return supportedLanguages.includes(value as Language);
}

/**
 * Which language a user sees (US-P1-OFF-06 AC1): the user's own choice, else the company default, else the first
 * supported browser language, else French. Region tags count ("fr-GN" → fr).
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

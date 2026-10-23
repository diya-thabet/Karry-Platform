import { isLanguage, supportedLanguages, useTranslation, type Language } from "@karry/i18n";
import { useTheme } from "../theme/ThemeProvider";
import { themeNames, type ThemeName } from "../theme/tokens";
import { SelectField } from "./SelectField";

const languageKey = "karry.language";

/** The user's own language choice on this device (null when they never chose). */
export function savedLanguage(): Language | null {
  try {
    const value = globalThis.localStorage.getItem(languageKey);
    return isLanguage(value) ? value : null;
  } catch {
    return null;
  }
}

/** Switches the language instantly (no reload: both catalogs are bundled) and remembers it (US-P1-OFF-06 AC1). */
export function LanguageSwitcher() {
  const { t, i18n } = useTranslation();
  return (
    <SelectField
      label={t("common.language")}
      value={i18n.language}
      onValueChange={(language) => {
        void i18n.changeLanguage(language);
        document.documentElement.lang = language;
        try {
          globalThis.localStorage.setItem(languageKey, language);
        } catch {
          // storage blocked: the choice lasts for this session
        }
      }}
      options={supportedLanguages.map((language) => ({ value: language, label: t(`common.language.${language}`) }))}
    />
  );
}

export function ThemeSwitcher() {
  const { t } = useTranslation();
  const { theme, setTheme } = useTheme();
  return (
    <SelectField
      label={t("common.theme")}
      value={theme}
      onValueChange={(value) => { setTheme(value as ThemeName); }}
      options={themeNames.map((name) => ({ value: name, label: t(`common.theme.${name}`) }))}
    />
  );
}

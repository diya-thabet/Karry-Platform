import { isLanguage, supportedLanguages, useTranslation, type I18n, type Language } from "@karry/i18n";
import { rememberChoice, rememberedChoice } from "../remembered";
import { useTheme } from "../theme/ThemeProvider";
import { themeNames, type ThemeName } from "../theme/tokens";
import { SelectField } from "./SelectField";

const languageKey = "karry.language";

/** The user's own language choice on this device (null when they never chose). */
export function savedLanguage(): Language | null {
  return rememberedChoice(languageKey, isLanguage);
}

/**
 * The person's language choice: applied at once (its catalog loads on first use; the page's `lang` and `dir` follow)
 * and remembered on this device.
 */
export function chooseLanguage(i18n: I18n, language: string): void {
  void i18n.changeLanguage(language);
  rememberChoice(languageKey, language);
}

/** Switches the language without a reload and remembers it (US-P1-OFF-06 AC1). */
export function LanguageSwitcher() {
  const { t, i18n } = useTranslation();
  return (
    <SelectField
      label={t("common.language")}
      value={i18n.language}
      onValueChange={(language) => { chooseLanguage(i18n, language); }}
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

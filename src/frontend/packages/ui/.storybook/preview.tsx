import type { Preview } from "@storybook/react-vite";
import { createI18n, I18nProvider, supportedLanguages } from "@karry/i18n";
import { LanguageDirection } from "../src/components/LanguageDirection";
import "../src/styles.css";
import { ThemeProvider } from "../src/theme/ThemeProvider";
import { themeNames, type ThemeName } from "../src/theme/tokens";

const i18n = await createI18n({ language: "fr" });

const preview: Preview = {
  globalTypes: {
    theme: { description: "Theme", toolbar: { title: "Theme", items: [...themeNames], dynamicTitle: true } },
    locale: { description: "Language", toolbar: { title: "Language", items: [...supportedLanguages], dynamicTitle: true } },
  },
  initialGlobals: { theme: "light", locale: "fr" },
  parameters: { a11y: { test: "error" } },
  decorators: [
    (Story, context) => {
      void i18n.changeLanguage(context.globals.locale as string);
      const density = context.parameters.density === "field" ? "field" : "office";
      return (
        <I18nProvider i18n={i18n}>
          <ThemeProvider key={`${String(context.globals.theme)}-${density}`} defaultTheme={context.globals.theme as ThemeName} density={density} remember={false}>
            <LanguageDirection><div className="p-4"><Story /></div></LanguageDirection>
          </ThemeProvider>
        </I18nProvider>
      );
    },
  ],
};

export default preview;

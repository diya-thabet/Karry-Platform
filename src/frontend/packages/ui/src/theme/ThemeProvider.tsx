import { createContext, use, useEffect, useMemo, useState, type ReactNode } from "react";
import { rememberChoice, rememberedChoice } from "../remembered";
import { cssVariables, themeNames, type ThemeName } from "./tokens";

export type Density = "office" | "field";

interface ThemeState {
  theme: ThemeName;
  setTheme: (theme: ThemeName) => void;
  density: Density;
}

const ThemeContext = createContext<ThemeState | null>(null);
const storageKey = "karry.theme";

const isTheme = (value: string | null): value is ThemeName => themeNames.includes(value as ThemeName);

/**
 * Applies a theme (CSS variables from tokens.ts and data-theme) and a density (data-density) to an element, the
 * document root by default. Switching is instant and remembered per device.
 */
export function ThemeProvider(props: {
  children: ReactNode;
  defaultTheme?: ThemeName;
  density?: Density;
  remember?: boolean;
  target?: HTMLElement;
}) {
  const { children, defaultTheme = "light", density = "office", remember = true, target } = props;
  const [theme, setThemeState] = useState<ThemeName>(() => (remember ? (rememberedChoice(storageKey, isTheme) ?? defaultTheme) : defaultTheme));

  useEffect(() => {
    const element = target ?? document.documentElement;
    element.setAttribute("data-theme", theme);
    element.setAttribute("data-density", density);
    for (const [name, value] of Object.entries(cssVariables(theme))) {
      element.style.setProperty(name, value);
    }
  }, [theme, density, target]);

  const value = useMemo<ThemeState>(
    () => ({
      theme,
      density,
      setTheme: (next) => {
        setThemeState(next);
        if (remember) {
          rememberChoice(storageKey, next);
        }
      },
    }),
    [theme, density, remember],
  );

  return <ThemeContext value={value}>{children}</ThemeContext>;
}

export function useTheme(): ThemeState {
  const state = use(ThemeContext);
  if (!state) {
    throw new Error("useTheme must be used inside <ThemeProvider>.");
  }

  return state;
}

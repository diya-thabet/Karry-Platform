/**
 * Design tokens (US-P1-OFF-05): the single source of every colour. Three themes:
 * - light and dark for the office portal (WCAG AA: text contrast >= 4.5:1);
 * - outdoor for tablets in sunlight, dust and gloves (WCAG AAA: text >= 7:1, borders >= 3:1).
 * Contrast is tested from these values (theme.test.ts), and the CSS variables are generated from them at runtime.
 */
export const themeNames = ["light", "dark", "outdoor"] as const;

export type ThemeName = (typeof themeNames)[number];

export interface ThemeTokens {
  background: string;
  surface: string;
  text: string;
  textMuted: string;
  border: string;
  primary: string;
  primaryText: string;
  danger: string;
  dangerText: string;
  success: string;
  warning: string;
  focus: string;
  /** Lines between rows and around cards: decorative (controls keep `border`, which has 3:1). */
  divider: string;
  /** Quiet areas on a surface: table headers, hovered rows, the sidebar's active item background behind text. */
  surfaceMuted: string;
  /** Tinted backgrounds behind coloured text: selection, status pills. */
  primarySoft: string;
  successSoft: string;
  warningSoft: string;
  dangerSoft: string;
}

export const themes: Record<ThemeName, ThemeTokens> = {
  light: {
    background: "#f4f6f8",
    surface: "#ffffff",
    text: "#1c1f23",
    textMuted: "#535a63",
    border: "#8a929c",
    primary: "#1f5fa8",
    primaryText: "#ffffff",
    danger: "#b3261e",
    dangerText: "#ffffff",
    success: "#1e6b38",
    warning: "#8a5300",
    focus: "#1f5fa8",
    divider: "#e2e5e9",
    surfaceMuted: "#f3f5f7",
    primarySoft: "#e8f0fb",
    successSoft: "#e6f4eb",
    warningSoft: "#fcf1df",
    dangerSoft: "#fbe8e6",
  },
  dark: {
    background: "#121417",
    surface: "#1d2126",
    text: "#eef0f2",
    textMuted: "#b2b9c1",
    border: "#7a838d",
    primary: "#8ab8ef",
    primaryText: "#0b1a2b",
    danger: "#ffb4ab",
    dangerText: "#3b0906",
    success: "#86d69c",
    warning: "#f2c46b",
    focus: "#8ab8ef",
    divider: "#2e343b",
    surfaceMuted: "#181b1f",
    primarySoft: "#1b2b3f",
    successSoft: "#15301f",
    warningSoft: "#372a10",
    dangerSoft: "#3a1916",
  },
  outdoor: {
    background: "#ffffff",
    surface: "#ffffff",
    text: "#000000",
    textMuted: "#2b2b2b",
    border: "#000000",
    primary: "#0033a0",
    primaryText: "#ffffff",
    danger: "#8c0000",
    dangerText: "#ffffff",
    success: "#00521f",
    warning: "#5c3700",
    focus: "#b35900",
    divider: "#000000",
    surfaceMuted: "#ffffff",
    primarySoft: "#ffffff",
    successSoft: "#ffffff",
    warningSoft: "#ffffff",
    dangerSoft: "#ffffff",
  },
};

/** WCAG 2.x relative luminance of a #rrggbb colour. */
export function luminance(hex: string): number {
  const channels = [1, 3, 5].map((i) => Number.parseInt(hex.slice(i, i + 2), 16) / 255);
  const [r, g, b] = channels.map((c) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4)) as [number, number, number];
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

/** WCAG contrast ratio (1–21) between two colours. */
export function contrast(a: string, b: string): number {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x) as [number, number];
  return (light + 0.05) / (dark + 0.05);
}

/** CSS custom properties of a theme: --karry-background, --karry-text-muted, … */
export function cssVariables(theme: ThemeName): Record<string, string> {
  return Object.fromEntries(
    Object.entries(themes[theme]).map(([name, value]) => [`--karry-${name.replace(/[A-Z]/g, (c) => `-${c.toLowerCase()}`)}`, value]),
  );
}

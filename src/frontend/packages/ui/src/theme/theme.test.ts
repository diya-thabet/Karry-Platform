import { contrast, cssVariables, themeNames, themes, type ThemeTokens } from "./tokens";

// Every text/background pair the components actually use.
const textPairs: [keyof ThemeTokens, keyof ThemeTokens][] = [
  ["text", "background"], ["text", "surface"], ["textMuted", "background"], ["textMuted", "surface"],
  ["primaryText", "primary"], ["dangerText", "danger"], ["primary", "surface"], ["danger", "surface"],
  ["success", "surface"], ["warning", "surface"],
];
const nonTextPairs: [keyof ThemeTokens, keyof ThemeTokens][] = [["border", "surface"], ["focus", "surface"], ["focus", "background"]];

describe("theme contrast (US-P1-OFF-05)", () => {
  it.each(themeNames.flatMap((theme) => textPairs.map(([fg, bg]) => [theme, fg, bg] as const)))(
    "%s: %s on %s meets AA (4.5:1), and AAA (7:1) in the outdoor theme",
    (theme, fg, bg) => {
      const required = theme === "outdoor" ? 7 : 4.5;
      expect(contrast(themes[theme][fg], themes[theme][bg])).toBeGreaterThanOrEqual(required);
    },
  );

  it.each(themeNames.flatMap((theme) => nonTextPairs.map(([fg, bg]) => [theme, fg, bg] as const)))(
    "%s: %s on %s is visible (3:1 for borders and focus rings)",
    (theme, fg, bg) => {
      expect(contrast(themes[theme][fg], themes[theme][bg])).toBeGreaterThanOrEqual(3);
    },
  );

  it("computes WCAG contrast correctly", () => {
    expect(contrast("#000000", "#ffffff")).toBeCloseTo(21, 5);
    expect(contrast("#ffffff", "#ffffff")).toBeCloseTo(1, 5);
    expect(contrast("#777777", "#ffffff")).toBeCloseTo(4.48, 2); // the classic "just below AA" grey
  });

  it("exposes every token as a CSS variable", () => {
    expect(cssVariables("outdoor")).toMatchObject({ "--karry-text-muted": "#2b2b2b", "--karry-primary-text": "#ffffff" });
    expect(Object.keys(cssVariables("light"))).toHaveLength(Object.keys(themes.light).length);
  });
});

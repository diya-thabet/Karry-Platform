import { composeStories } from "@storybook/react-vite";
import { cleanup, render } from "@testing-library/react";
import axe from "axe-core";
import { createI18n, I18nProvider } from "@karry/i18n";
import "./styles.css";
import { ThemeProvider } from "./theme/ThemeProvider";
import { themeNames } from "./theme/tokens";

// TC-P1-OFF-006: every story of the design system, in every theme, rendered in a real Chromium (layout and colour
// contrast are real here), checked with axe-core. Zero serious or critical violations allowed.
const modules = import.meta.glob<Record<string, unknown>>("./components/*.stories.tsx", { eager: true });
const stories = Object.entries(modules).flatMap(([file, module]) =>
  Object.entries(composeStories(module as Parameters<typeof composeStories>[0])).map(([name, Story]) => ({
    id: `${file.replace("./components/", "").replace(".stories.tsx", "")}/${name}`,
    Story: Story as () => React.JSX.Element,
    density: (Story as { parameters?: { density?: string } }).parameters?.density === "field" ? ("field" as const) : ("office" as const),
  })),
);
const i18n = await createI18n({ language: "fr" });

afterEach(() => { cleanup(); });

function show(Story: () => React.JSX.Element, theme: (typeof themeNames)[number], density: "office" | "field") {
  return render(
    <I18nProvider i18n={i18n}>
      <ThemeProvider key={`${theme}-${density}`} defaultTheme={theme} density={density} remember={false}>
        <main><Story /></main>
      </ThemeProvider>
    </I18nProvider>,
  );
}

describe("accessibility of every story in every theme (TC-P1-OFF-006)", () => {
  it("finds the stories", () => {
    expect(stories.length).toBeGreaterThanOrEqual(15);
  });

  it.each(themeNames.flatMap((theme) => stories.map((story) => [theme, story.id, story] as const)))(
    "%s · %s: no serious or critical axe violation",
    async (theme, _id, { Story, density }) => {
      show(Story, theme, density);
      await new Promise((resolve) => setTimeout(resolve, 50)); // let portals and effects settle

      const result = await axe.run(document.body, { resultTypes: ["violations"] });
      const serious = result.violations
        .filter((v) => v.impact === "serious" || v.impact === "critical")
        .map((v) => `${v.id} (${v.impact ?? "?"}): ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
      expect(serious).toEqual([]);
    },
  );
});

describe("field density (US-P1-OFF-05 AC2)", () => {
  it.each(themeNames)("%s: every control is at least 48 px high and body text at least 16 px", (theme) => {
    const field = stories.find((s) => s.id === "Fields/FieldControls");
    if (!field) throw new Error("story Fields/FieldControls is missing");
    show(field.Story, theme, "field");

    const controls = [...document.querySelectorAll<HTMLElement>("input, button, [role=combobox]")];
    expect(controls.length).toBeGreaterThan(0);
    for (const control of controls) {
      expect(control.getBoundingClientRect().height, control.outerHTML.slice(0, 60)).toBeGreaterThanOrEqual(48);
    }

    expect(Number.parseFloat(getComputedStyle(document.body).fontSize)).toBeGreaterThanOrEqual(16);
    for (const label of document.querySelectorAll("label")) {
      expect(Number.parseFloat(getComputedStyle(label).fontSize)).toBeGreaterThanOrEqual(16);
    }
  });
});

import { composeStories } from "@storybook/react-vite";
import { cleanup, render } from "@testing-library/react";
import axe from "axe-core";
import { page } from "vitest/browser";
import { createI18n, I18nProvider } from "@karry/i18n";
import "./styles.css";
import { LanguageDirection } from "./components/LanguageDirection";
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
// axe checks what people read once a dialog or menu has opened, not a frame of its fade-in (colour contrast mid-fade).
const still = document.createElement("style");
still.textContent = "*, ::before, ::after { animation: none !important; transition: none !important; }";
document.head.append(still);

afterEach(() => { cleanup(); });

function show(Story: () => React.JSX.Element, theme: (typeof themeNames)[number], density: "office" | "field") {
  return render(
    <I18nProvider i18n={i18n}>
      <ThemeProvider key={`${theme}-${density}`} defaultTheme={theme} density={density} remember={false}>
        <LanguageDirection><main><Story /></main></LanguageDirection>
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

describe("right to left: Arabic and N'Ko", () => {
  afterAll(async () => { await i18n.changeLanguage("fr"); });

  it.each((["ar", "nqo"] as const).flatMap((language) => stories.map((story) => [language, story.id, story] as const)))(
    "%s · %s: no serious or critical axe violation, the page mirrored",
    async (language, _id, { Story, density }) => {
      await i18n.changeLanguage(language);
      show(Story, "light", density);
      await new Promise((resolve) => setTimeout(resolve, 50));

      expect(document.documentElement.dir).toBe("rtl");
      const result = await axe.run(document, { resultTypes: ["violations"] });
      const serious = result.violations
        .filter((v) => v.impact === "serious" || v.impact === "critical")
        .map((v) => `${v.id} (${v.impact ?? "?"}): ${v.nodes.map((n) => n.target.join(" ")).join(", ")}`);
      expect(serious).toEqual([]);
    },
  );

  it("opens the drawer from the right and puts a table's numbers on the left", async () => {
    await i18n.changeLanguage("ar");
    const drawer = stories.find((s) => s.id === "Layout/MobileDrawer");
    const table = stories.find((s) => s.id === "Data/Table");
    if (!drawer || !table) throw new Error("stories Layout/MobileDrawer and Data/Table are needed");

    show(drawer.Story, "light", "office");
    const panel = document.querySelector<HTMLElement>(".karry-drawer");
    expect(panel).not.toBeNull();
    expect(Math.round(panel?.getBoundingClientRect().right ?? 0)).toBe(document.documentElement.clientWidth);
    cleanup();

    await page.viewport(1280, 800); // the office layout: on a phone every cell is a label and its value
    show(table.Story, "light", "office");
    const numbers = [...document.querySelectorAll<HTMLElement>("td.tabular-nums, th.tabular-nums")];
    expect(numbers.length).toBeGreaterThan(0);
    for (const cell of numbers) {
      expect(["end", "left"]).toContain(getComputedStyle(cell).textAlign); // the end of an Arabic line is its left
    }
    await page.viewport(414, 896);
  });
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

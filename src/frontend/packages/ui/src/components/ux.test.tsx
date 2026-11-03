import { act, fireEvent, render, screen, within } from "@testing-library/react";
import { SearchX, Truck } from "lucide-react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { rememberChoice, rememberedChoice } from "../remembered";
import { Breadcrumbs } from "./Breadcrumbs";
import { Checkbox } from "./Checkbox";
import { DataTable } from "./DataTable";
import { StatCard } from "./Layout";
import { Menu } from "./Menu";
import { PageSkeleton } from "./Skeleton";
import { EmptyState } from "./States";
import { ToastProvider, useToast } from "./Toast";

// The fixes of the UI/UX review (docs/05-quality/ui-ux-review.md), one block per finding.
const i18n = await createI18n({ language: "en" });

describe("Menu: a setting is one row with its value, its values in a submenu (UX-01, UX-02)", () => {
  function AccountMenu({ onLanguage = vi.fn(), onSignOut = vi.fn() }: { onLanguage?: (value: string) => void; onSignOut?: () => void }) {
    return (
      <Menu
        label="Account"
        trigger={<button type="button">Account</button>}
        header={<span>Karim Trabelsi</span>}
        groups={[
          {
            items: [{
              kind: "choice", key: "language", label: "Language", value: "fr", onValueChange: onLanguage,
              options: [{ value: "fr", label: "Français", lang: "fr" }, { value: "en", label: "English", lang: "en" }, { value: "ar", label: "العربية", lang: "ar" }],
            }],
          },
          { items: [{ key: "out", label: "Sign out", onSelect: onSignOut }] },
        ]}
      />
    );
  }

  function open() {
    fireEvent.keyDown(screen.getByRole("button", { name: "Account" }), { key: "Enter" });
    return screen.getByRole("menu");
  }

  it("shows the header, the setting with its current value and the actions, not every value", () => {
    render(<AccountMenu />);
    const menu = open();

    expect(within(menu).getByText("Karim Trabelsi")).toBeInTheDocument();
    expect(within(menu).getAllByRole("menuitem").map((item) => item.textContent)).toEqual(["LanguageFrançais", "Sign out"]);
    expect(within(menu).getByText("Français")).toHaveAttribute("lang", "fr");
    expect(screen.queryByRole("menuitemradio")).not.toBeInTheDocument();
  });

  it("opens the values with the keyboard, says which one is chosen and reports a new choice", async () => {
    const onLanguage = vi.fn();
    render(<AccountMenu onLanguage={onLanguage} />);
    fireEvent.keyDown(within(open()).getByRole("menuitem", { name: /^Language/ }), { key: "ArrowRight" });

    const values = await screen.findAllByRole("menuitemradio");
    expect(values.map((value) => [value.textContent, value.getAttribute("aria-checked"), value.getAttribute("lang")])).toEqual([
      ["Français", "true", "fr"], ["English", "false", "en"], ["العربية", "false", "ar"],
    ]);
    fireEvent.click(screen.getByRole("menuitemradio", { name: "English" }));
    expect(onLanguage).toHaveBeenCalledWith("en");
  });

  it("runs an action", () => {
    const onSignOut = vi.fn();
    render(<AccountMenu onSignOut={onSignOut} />);
    fireEvent.click(within(open()).getByRole("menuitem", { name: "Sign out" }));

    expect(onSignOut).toHaveBeenCalledTimes(1);
  });
});

describe("Breadcrumbs (UX-04)", () => {
  it("is a named navigation listing the sections above the page in order, separators hidden from screen readers", () => {
    render(<Breadcrumbs label="Breadcrumb"><a href="/settings">Settings</a><a href="/settings/materials">Materials</a></Breadcrumbs>);

    const trail = screen.getByRole("navigation", { name: "Breadcrumb" });
    expect(within(trail).getAllByRole("listitem").map((item) => item.textContent)).toEqual(["Settings", "Materials"]);
    expect(within(trail).getAllByRole("link").map((link) => link.getAttribute("href"))).toEqual(["/settings", "/settings/materials"]);
    expect(trail.querySelectorAll("svg[aria-hidden]")).toHaveLength(1); // one separator, between the two
  });
});

describe("DataTable while loading (UX-07, UX-12)", () => {
  const columns = [{ key: "code", header: "Code", cell: (r: { id: string }) => r.id }, { key: "hours", header: "Hours", cell: () => "1", numeric: true }];

  it("keeps its shape with skeleton rows, busy and named, instead of the empty state", () => {
    render(<I18nProvider i18n={i18n}><DataTable caption="Machines" columns={columns} rows={[]} loading rowKey={(r) => r.id} empty={<EmptyState title="No machines" />} /></I18nProvider>);

    const table = screen.getByRole("table", { name: "Machines" });
    expect(table).toHaveAttribute("aria-busy", "true");
    expect(within(table).getAllByRole("row")).toHaveLength(4); // the header and three skeleton rows
    expect(within(table).getByText("Loading…")).toHaveClass("sr-only");
    expect(screen.queryByRole("heading", { name: "No machines" })).not.toBeInTheDocument();
  });

  it("shows the rows once they are there, even if more are loading", () => {
    render(<DataTable caption="Machines" columns={columns} rows={[{ id: "EX-01" }]} loading rowKey={(r) => r.id} empty={null} />);

    const table = screen.getByRole("table", { name: "Machines" });
    expect(table).not.toHaveAttribute("aria-busy");
    expect(within(table).getAllByRole("row")).toHaveLength(2);
  });

  it("aligns each header like its cells: start for text, end for numbers (browsers centre headers)", () => {
    render(<DataTable caption="Machines" columns={columns} rows={[{ id: "EX-01" }]} rowKey={(r) => r.id} empty={null} />);

    const [code, hours] = screen.getAllByRole("columnheader");
    expect(code).toHaveClass("text-start");
    expect(hours).toHaveClass("text-end");
  });
});

describe("loading figures and pages (UX-13, UX-14)", () => {
  it("a figure not known yet is a busy skeleton, not an ellipsis", () => {
    render(<StatCard icon={Truck} label="Machines" value={undefined} />);

    expect(screen.queryByText("…")).not.toBeInTheDocument();
    expect(document.querySelector("[aria-busy=true]")).not.toBeNull();
  });

  it("a page skeleton is a busy status saying it loads", () => {
    render(<I18nProvider i18n={i18n}><PageSkeleton /></I18nProvider>);

    const status = screen.getByRole("status");
    expect(status).toHaveAttribute("aria-busy", "true");
    expect(status).toHaveTextContent("Loading…");
  });
});

describe("EmptyState (UX-21)", () => {
  it("takes the icon that says which kind of nothing", () => {
    const { container } = render(<EmptyState icon={SearchX} title="Not found" />);

    expect(container.querySelector("svg")).toHaveClass("lucide-search-x");
  });
});

describe("Checkbox (UX-17)", () => {
  it("is a real check box named by its label, toggled by clicking the label", () => {
    const change = vi.fn();
    render(<Checkbox label="Show archived sites" onChange={change} />);

    const box = screen.getByRole("checkbox", { name: "Show archived sites" });
    fireEvent.click(screen.getByText("Show archived sites"));
    expect(box).toBeChecked();
    expect(change).toHaveBeenCalledTimes(1);
  });
});

describe("Toast (UX-15)", () => {
  function Saver({ status }: { status: "ok" | "error" }) {
    const toast = useToast();
    return <button type="button" onClick={() => { toast({ status, title: "Site saved" }); }}>Save</button>;
  }

  it.each([["ok", "lucide-circle-check"], ["error", "lucide-circle-x"]] as const)("%s: says its status with an icon too, and can be closed", async (status, icon) => {
    render(<I18nProvider i18n={i18n}><ToastProvider label="Notifications"><Saver status={status} /></ToastProvider></I18nProvider>);
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const toast = (await screen.findByText("Site saved")).closest("li");
    if (!toast) throw new Error("toast not rendered");
    expect(toast.querySelector("svg")?.getAttribute("class")).toContain(icon);
    await act(async () => { fireEvent.click(within(toast).getByRole("button", { name: "Close" })); await Promise.resolve(); });
    await vi.waitFor(() => { expect(screen.queryByText("Site saved")).not.toBeInTheDocument(); });
  });
});

describe("remembered choices (UX-24)", () => {
  const isColour = (value: string | null): value is "red" | "blue" => value === "red" || value === "blue";
  afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });

  it("reads back a remembered choice and ignores an unknown value", () => {
    rememberChoice("karry.test", "blue");
    expect(rememberedChoice("karry.test", isColour)).toBe("blue");

    localStorage.setItem("karry.test", "green");
    expect(rememberedChoice("karry.test", isColour)).toBeNull();
  });

  it("never fails when the browser blocks storage (private mode, locked tablets)", () => {
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked"); });
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked"); });

    expect(() => { rememberChoice("karry.test", "red"); }).not.toThrow();
    expect(rememberedChoice("karry.test", isColour)).toBeNull();
  });
});

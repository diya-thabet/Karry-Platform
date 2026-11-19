import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { checklist, fakeApi } from "../testing/fakeApi";
import { checkItems, codeFrom, itemsBody, languagesFor, newItem } from "./api";

const manager = ["fieldops.checklists.read", "fieldops.checklists.manage", "assets.registry.read"];
const reader = ["fieldops.checklists.read"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("checklist rules", () => {
  it("makes a code from the first Latin text when none is typed", () => {
    expect(codeFrom({ ...newItem(), texts: { fr: "Freins / frein à main" } }, 0)).toBe("freins-frein-a-main");
    expect(codeFrom({ ...newItem(), texts: { ar: "الفرامل" } }, 2)).toBe("item-3");
    expect(codeFrom({ ...newItem(), code: " Horn ", texts: { en: "Horn" } }, 0)).toBe("horn");
  });

  it("finds duplicates, missing texts and too long texts before anything is sent", () => {
    const a = { ...newItem(), texts: { en: "Brakes" } };
    const b = { ...newItem(), code: "brakes", texts: { fr: "Freins" } };
    const c = { ...newItem(), texts: { en: "  " } };
    const d = { ...newItem(), texts: { en: "x".repeat(201) } };
    expect(checkItems([a, b, c, d]).items).toEqual({ [b.key]: "duplicate", [c.key]: "text", [d.key]: "tooLong" });
    expect(checkItems([]).list).toBe("none");
  });

  it("sends trimmed texts only and always a photo for a critical item", () => {
    expect(itemsBody([{ ...newItem(), texts: { en: " Brakes ", fr: "" }, critical: true, photoOnNotOk: false }]))
      .toEqual([{ code: "brakes", texts: { en: "Brakes" }, critical: true, photoOnNotOk: true }]);
  });

  it("puts the company's language first", () => {
    expect(languagesFor("ar")).toEqual(["ar", "fr", "en", "emk", "nqo"]);
    expect(languagesFor("none")).toEqual(["fr", "en", "ar", "emk", "nqo"]);
  });
});

describe("checklist builder (US-P2-FO-03)", () => {
  it("lists the newest version per machine type, reachable from settings, read-only for a reader", async () => {
    await renderAt("/settings", fakeApi({ permissions: reader, checklists: [checklist(), checklist({ id: "0192f0c1-0000-7000-8000-000000008002", version: 2, required: false })] }));

    fireEvent.click(await screen.findByRole("link", { name: "Pre-start checklists" }));

    const row = (await screen.findByRole("link", { name: "Excavator" })).closest("tr");
    expect(within(row as HTMLElement).getAllByText("2")).toHaveLength(2); // version 2, two items
    expect(within(row as HTMLElement).getByText("Optional")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New checklist" })).not.toBeInTheDocument();
  });

  it("creates the first version of a machine type's checklist (AC1)", async () => {
    const api = fakeApi({ permissions: manager, checklists: [checklist()] });
    await renderAt("/settings/checklists/new", api);

    fireEvent.click(await screen.findByRole("combobox", { name: /^Machine type/ }));
    expect(screen.queryByRole("option", { name: "Excavator" })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole("option", { name: "Haul truck" }));
    fireEvent.change(screen.getByRole("textbox", { name: "Text in Français" }), { target: { value: "Pneus à plat ?" } });
    fireEvent.change(screen.getByRole("textbox", { name: "Text in English" }), { target: { value: "Flat tyres?" } });
    fireEvent.click(screen.getByRole("checkbox", { name: "Critical: a Not OK stops the machine" }));
    expect(screen.getByRole("checkbox", { name: "Photo on Not OK" })).toBeChecked();
    fireEvent.click(screen.getByRole("button", { name: "Publish version 1" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Haul truck" })).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "POST", path: "/api/v1/fieldops/checklists/haul-truck/versions",
      body: { required: true, items: [{ code: "pneus-a-plat", texts: { fr: "Pneus à plat ?", en: "Flat tyres?" }, critical: true, photoOnNotOk: true }] },
    }]);
  });

  it("edits the newest version into the next one, keeping the older one in the list (AC2)", async () => {
    const api = fakeApi({ permissions: manager, checklists: [checklist()] });
    await renderAt("/settings/checklists/excavator", api);

    const second = await screen.findByRole("listitem", { name: "Item 2" });
    fireEvent.click(within(second).getByRole("button", { name: "Move item 2 up" }));
    fireEvent.click(screen.getByRole("button", { name: "Add an item" }));
    fireEvent.change(within(screen.getByRole("listitem", { name: "Item 3" })).getByRole("textbox", { name: "Text in English" }), { target: { value: "Horn" } });
    fireEvent.click(screen.getByRole("checkbox", { name: "Required before starting a shift" }));
    fireEvent.click(screen.getByRole("button", { name: "Publish version 2" }));

    expect(await screen.findByText("Version 2 published")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Publish version 3" })).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "POST", path: "/api/v1/fieldops/checklists/excavator/versions",
      body: {
        required: false,
        items: [
          { code: "lights", texts: { fr: "Feux", en: "Lights" }, critical: false, photoOnNotOk: false },
          { code: "brakes", texts: { fr: "Freins", en: "Brakes" }, critical: true, photoOnNotOk: true },
          { code: "horn", texts: { en: "Horn" }, critical: false, photoOnNotOk: false },
        ],
      },
    }]);
    const versions = screen.getByRole("table", { name: "Versions" });
    expect(within(versions).getByText("lights, brakes, horn")).toBeInTheDocument();
    expect(within(versions).getByText("brakes, lights")).toBeInTheDocument();
  });

  it("refuses an item without text or with a duplicate code before asking the API", async () => {
    const api = fakeApi({ permissions: manager, checklists: [checklist()] });
    await renderAt("/settings/checklists/excavator", api);

    fireEvent.click(await screen.findByRole("button", { name: "Add an item" }));
    fireEvent.change(within(screen.getByRole("listitem", { name: "Item 3" })).getByRole("textbox", { name: "Code" }), { target: { value: "brakes" } });
    fireEvent.click(screen.getByRole("button", { name: "Publish version 2" }));

    expect(await screen.findByText("Another item has this code.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);
  });
});

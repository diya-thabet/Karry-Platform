import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, meterOf, site } from "../testing/fakeApi";
import { readingText } from "./api";

const manager = ["assets.registry.read", "assets.registry.manage", "organization.sites.read"];
const reader = ["assets.registry.read", "organization.sites.read"];
const nord = site();
const sud = site({ id: "0192f0c1-0000-7000-8000-000000005002", code: "CAR-SUD", name: "Carrière Sud" });
const pel = asset();
const truck = asset({ id: "0192f0c1-0000-7000-8000-000000007002", code: "TOMB-01", name: "Tombereau 40 t", type: "haul-truck", plate: "123 TU 4567", siteId: sud.id });
const old = asset({ id: "0192f0c1-0000-7000-8000-000000007003", code: "OLD-01", name: "Vieille chargeuse", status: "disposed" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function choose(label: string | RegExp, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

describe("machines (US-P1-AST-01, US-P1-AST-02, US-P1-AST-03)", () => {
  it("lists the fleet with sites and statuses, searches, and shows disposed machines only on request", async () => {
    const api = fakeApi({ permissions: reader, sites: [nord, sud], assets: [pel, truck, old] });
    await renderAt("/assets", api);

    expect(await screen.findByRole("link", { name: "Pelle Cat 336" })).toHaveAttribute("href", `/assets/${pel.id}`);
    const row = screen.getByRole("link", { name: "Tombereau 40 t" }).closest("tr");
    expect(row).not.toBeNull();
    expect(await within(row as HTMLElement).findByText("Carrière Sud")).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText("Haul truck")).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText("123 TU 4567")).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Plate or serial" })).toBeInTheDocument();
    expect(screen.getByText(pel.serialNumber ?? "")).toBeInTheDocument(); // no plate: the serial identifies it
    expect(screen.queryByText("Vieille chargeuse")).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New machine" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Print labels" })).not.toBeInTheDocument();

    fireEvent.change(screen.getByRole("searchbox", { name: /Search/ }), { target: { value: "zz" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    expect(await screen.findByText("No machines found")).toBeInTheDocument();
    fireEvent.change(screen.getByRole("searchbox", { name: /Search/ }), { target: { value: "tu 45" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await screen.findByRole("link", { name: "Tombereau 40 t" });
    expect(screen.queryByRole("link", { name: "Pelle Cat 336" })).not.toBeInTheDocument();
    expect(api.sent.at(-1)?.path).toBe("/api/v1/assets");

    fireEvent.change(screen.getByRole("searchbox", { name: /Search/ }), { target: { value: "" } });
    fireEvent.click(screen.getByRole("button", { name: "Search" }));
    await choose("Status", "Disposed");
    expect(await screen.findByRole("link", { name: "Vieille chargeuse" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Pelle Cat 336" })).not.toBeInTheDocument();
  });

  it("registers a machine on a chosen site and opens it (AC1)", async () => {
    const api = fakeApi({ permissions: manager, sites: [nord, sud] });
    await renderAt("/assets/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Code/ }), { target: { value: "pel-02" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Name/ }), { target: { value: "Pelle Volvo" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Plate/ }), { target: { value: "ab 12" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the machine" }));
    expect(await screen.findByText("Choose the site the machine is on.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);

    await choose(/^Site/, "Carrière Sud");
    fireEvent.change(screen.getByRole("textbox", { name: /^Year/ }), { target: { value: "21" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the machine" }));
    expect(await screen.findByText("Enter a year with 4 digits.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);

    fireEvent.change(screen.getByRole("textbox", { name: /^Year/ }), { target: { value: "2024" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the machine" }));

    expect(await screen.findByRole("heading", { level: 1, name: "PEL-02 · Pelle Volvo" })).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "POST", path: "/api/v1/assets",
      body: {
        code: "PEL-02", name: "Pelle Volvo", type: "excavator", make: null, model: null, serialNumber: null, plate: "AB 12", year: 2024,
        acquiredOn: null, siteId: sud.id,
      },
    }]);
  });

  it("shows a machine read-only to a reader: details, sites, label and meter history, without any change button", async () => {
    const meter = meterOf();
    const api = fakeApi({
      permissions: reader, sites: [nord, sud],
      assets: [{ ...pel, sites: [{ siteId: sud.id, from: "2025-01-01T08:00:00Z" }, { siteId: nord.id, from: "2026-01-05T08:00:00Z" }] }],
      meters: { [pel.id]: [meter] },
      readings: { [meter.id]: [{ id: "r2", value: 4250.5, readAt: "2026-10-26T16:00:00Z", recordedAt: "2026-10-26T16:01:00Z", recordedBy: "x" }] },
    });
    await renderAt(`/assets/${pel.id}`, api);

    expect(await screen.findByRole("heading", { level: 1, name: "PEL-01 · Pelle Cat 336" })).toBeInTheDocument();
    expect(screen.getByText("You can see the machines but not change them.")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /^Name/ })).toBeDisabled();
    expect(screen.getByText(pel.label)).toBeInTheDocument();
    const history = screen.getByRole("table", { name: "Sites the machine has been on" });
    await within(history).findByText("Carrière Nord");
    expect(within(history).getAllByRole("row").slice(1).map((row) => (row as HTMLTableRowElement).cells[0]?.textContent)).toEqual(["Carrière Nord", "Carrière Sud"]);
    await vi.waitFor(() => {
      expect(screen.getAllByText("4,250.5 h")).toHaveLength(2); // last reading, then the history
    });
    expect(screen.getByText("No photo yet.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /Save|Print|Reissue|Record|Move/ })).not.toBeInTheDocument();
    expect(screen.queryByText("Choose a photo")).not.toBeInTheDocument();

    const results = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("records a meter reading and shows the API's refusal of a reading that goes backwards (AC1, TC-P1-AST-002)", async () => {
    const meter = meterOf();
    const api = fakeApi({ permissions: manager, sites: [nord], assets: [pel], meters: { [pel.id]: [meter] } });
    await renderAt(`/assets/${pel.id}`, api);

    const field = await screen.findByRole("textbox", { name: /New reading \(h\)/ });
    expect(screen.getByText("At least the last reading, 4,250.5 h.")).toBeInTheDocument();
    fireEvent.change(field, { target: { value: "4300,25" } });
    fireEvent.click(screen.getByRole("button", { name: "Record" }));
    expect(await screen.findByText("Enter a number with at most one decimal.")).toBeInTheDocument();

    fireEvent.change(field, { target: { value: "4 300,5" } });
    fireEvent.click(screen.getByRole("button", { name: "Record" }));
    expect(await screen.findByText("Reading recorded.")).toBeInTheDocument();
    await vi.waitFor(() => {
      expect(screen.getAllByText("4,300.5 h")).toHaveLength(2);
    });

    fireEvent.change(screen.getByRole("textbox", { name: /New reading/ }), { target: { value: "4000" } });
    fireEvent.click(screen.getByRole("button", { name: "Record" }));
    expect(await screen.findByText("The reading is below the last one. A meter only goes forward.")).toBeInTheDocument();
    expect(api.writes().map((w) => [w.path, w.body])).toEqual([
      [`/api/v1/assets/${pel.id}/meters/${meter.id}/readings`, { value: "4300.5", readAt: null }],
      [`/api/v1/assets/${pel.id}/meters/${meter.id}/readings`, { value: "4000", readAt: null }],
    ]);
  });

  it("moves a machine, then changes its status", async () => {
    const api = fakeApi({ permissions: manager, sites: [nord, sud], assets: [pel] });
    await renderAt(`/assets/${pel.id}`, api);

    expect(await screen.findByRole("button", { name: "Move" })).toBeDisabled();
    await choose("Move to", "Carrière Sud");
    fireEvent.click(screen.getByRole("button", { name: "Move" }));
    expect(await screen.findByText("Machine moved.")).toBeInTheDocument();
    await choose("Status", "In repair");
    expect(await screen.findByText("Status saved.")).toBeInTheDocument();

    expect(api.writes().map((w) => [w.path, w.body])).toEqual([
      [`/api/v1/assets/${pel.id}/move`, { siteId: sud.id }],
      [`/api/v1/assets/${pel.id}/status`, { status: "in-repair" }],
    ]);
  });

  it("uploads the photo as the image itself and reissues the label after a confirmation", async () => {
    const api = fakeApi({ permissions: manager, sites: [nord], assets: [pel] });
    await renderAt(`/assets/${pel.id}`, api);

    const input = (await screen.findByText("Choose a photo")).querySelector("input");
    expect(input).toHaveAttribute("accept", "image/jpeg,image/png,image/webp");
    if (!input) {
      throw new Error("no file input");
    }

    fireEvent.change(input, { target: { files: [new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], "pel.png", { type: "image/png" })] } });
    expect(await screen.findByText("Photo saved.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Remove the photo" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Reissue the label" }));
    expect(api.writes().filter((w) => w.path.endsWith("/label"))).toEqual([]);
    fireEvent.click(await screen.findByRole("button", { name: "Reissue" }));
    expect(await screen.findByText("KRY1:NEWLABELTOKENAAAAAAAAAAA")).toBeInTheDocument();

    expect(api.writes().map((w) => [w.method, w.path, w.body])).toEqual([
      ["PUT", `/api/v1/assets/${pel.id}/photo`, null],
      ["POST", `/api/v1/assets/${pel.id}/label`, null],
    ]);
  });

  it("prints the listed machines' labels, skipping labels already used on the sheet (AC2)", async () => {
    const api = fakeApi({ permissions: manager, sites: [nord, sud], assets: [pel, truck] });
    await renderAt("/assets", api);

    fireEvent.click(await screen.findByRole("button", { name: "Print labels" }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/^2 labels on A4 sheets/)).toBeInTheDocument();
    const skip = within(dialog).getByRole("textbox", { name: /already used/ });
    fireEvent.change(skip, { target: { value: "24" } });
    expect(within(dialog).getByRole("button", { name: "Make the PDF" })).toBeDisabled();
    fireEvent.change(skip, { target: { value: "5" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Make the PDF" }));

    expect(await within(dialog).findByText(/The labels are ready/)).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "POST", path: "/api/v1/assets/labels/sheet", body: { assetIds: [pel.id, truck.id], skip: 5 } }]);
  });
});

describe("readingText", () => {
  it.each([["1234", "1234"], ["1 234,5", "1234.5"], [" 0.5 ", "0.5"]])("reads %j as %j", (typed, expected) => {
    expect(readingText(typed)).toBe(expected);
  });

  it.each(["", "-1", "1.25", "12345678", "1e3", "abc"])("refuses %j", (typed) => {
    expect(readingText(typed)).toBeNull();
  });
});

import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, material } from "../testing/fakeApi";
import { addDays, todayIn } from "../sites/api";
import { decimalText, looseFromSwell, shiftDecimal, swellPercent } from "./api";

const manager = ["materials.catalog.read", "materials.catalog.manage"];
const reader = ["materials.catalog.read"];
const materialId = material().id;

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("decimal helpers", () => {
  it("keeps typed decimals exact and shifts percent to fraction without rounding", () => {
    expect(decimalText("12,5")).toBe("12.5");
    expect(decimalText("1.2.3")).toBeNull();
    expect(decimalText("-1")).toBeNull();
    expect(shiftDecimal("3", -2)).toBe("0.03");
    expect(shiftDecimal("4.5", -2)).toBe("0.045");
    expect(shiftDecimal("0.045", 2)).toBe("4.5");
    expect(shiftDecimal("0.25", 2)).toBe("25");
    expect(shiftDecimal("0", -2)).toBe("0");
  });

  it("derives swell from bank and loose and loose from swell (ADR-0025)", () => {
    expect(swellPercent("2.2", "1.6")).toBe("37.5");
    expect(looseFromSwell("2.2", "37.5")).toBe("1.6");
    expect(looseFromSwell("2.56", "60")).toBe("1.6");
  });
});

describe("materials (US-P1-AST-04)", () => {
  it("lists the materials with the densities valid today, reachable from settings", async () => {
    await renderAt("/settings", fakeApi({ permissions: reader, materials: [material()] }));

    fireEvent.click(await screen.findByRole("link", { name: "Materials" }));

    const row = (await screen.findByRole("link", { name: "Grave 0/31.5" })).closest("tr");
    expect(row).not.toBeNull();
    expect(within(row as HTMLElement).getByText("2.2")).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText("1.6")).toBeInTheDocument();
    expect(within(row as HTMLElement).getByText("Product")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New material" })).not.toBeInTheDocument();
  });

  it("creates a material with densities typed in percent, filling loose from the swell (AC1)", async () => {
    const api = fakeApi({ permissions: manager });
    await renderAt("/settings/materials/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Code/ }), { target: { value: "cal-1" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Name/ }), { target: { value: "Calcaire" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Bank density/ }), { target: { value: "2,56" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Swell/ }), { target: { value: "60" } });
    expect(screen.getByRole("textbox", { name: /^Loose density/ })).toHaveValue("1.6");
    fireEvent.change(screen.getByRole("textbox", { name: /^Moisture/ }), { target: { value: "4,5" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the material" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Calcaire" })).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "POST", path: "/api/v1/materials",
      body: {
        code: "CAL-1", name: "Calcaire", kind: "raw-rock", effectiveFrom: todayIn("UTC"),
        densities: { bank: "2.56", loose: "1.6", compacted: null, moisture: "0.045" },
      },
    }]);
  });

  it("refuses a material without densities before asking the API", async () => {
    const api = fakeApi({ permissions: manager });
    await renderAt("/settings/materials/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Code/ }), { target: { value: "X" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the material" }));

    expect(await screen.findByText("Give at least one density.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);
  });

  it("adds a density version from tomorrow and removes it; the current one cannot be removed (AC2)", async () => {
    const api = fakeApi({ permissions: manager, materials: [material()] });
    await renderAt(`/settings/materials/${materialId}`, api);
    const tomorrow = addDays(todayIn("UTC"), 1);

    fireEvent.click(await screen.findByRole("button", { name: "New density version" }));
    fireEvent.change(screen.getByRole("textbox", { name: /^Loose density/ }), { target: { value: "1.65" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the version" }));

    expect(await screen.findByText("Density version saved.")).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "PUT", path: `/api/v1/materials/${materialId}/densities/${tomorrow}`,
      body: { bank: "2.2", loose: "1.65", compacted: null, moisture: "0.03" },
    }]);
    expect(await screen.findByText("Planned")).toBeInTheDocument();
    // Only the planned version: getBy fails if the current one also offered removal.
    fireEvent.click(screen.getByRole("button", { name: /^Remove the version/ }));
    expect(await screen.findByText("Density version removed.")).toBeInTheDocument();
    expect(api.writes().at(-1)).toEqual({ method: "DELETE", path: `/api/v1/materials/${materialId}/densities/${tomorrow}`, body: null });
  });

  it("refuses a version starting today", async () => {
    const api = fakeApi({ permissions: manager, materials: [material()] });
    await renderAt(`/settings/materials/${materialId}`, api);

    fireEvent.click(await screen.findByRole("button", { name: "New density version" }));
    fireEvent.change(screen.getByLabelText(/^Valid from/), { target: { value: todayIn("UTC") } });
    fireEvent.click(screen.getByRole("button", { name: "Save the version" }));

    expect(await screen.findByText("Choose a date after today.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);
  });

  it("shows the material read-only without the manage permission", async () => {
    await renderAt(`/settings/materials/${materialId}`, fakeApi({ permissions: reader, materials: [material()] }));

    expect(await screen.findByText("You can see the materials but not change them.")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /^Name/ })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "New density version" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Archive the material" })).not.toBeInTheDocument();
  });
});

describe("conversions (US-P1-UNI-01 AC4)", () => {
  it("converts with the API, toggles unit and state, and shows the factors used on tap", async () => {
    const api = fakeApi({ permissions: reader, materials: [material()] });
    await renderAt(`/settings/materials/${materialId}`, api);
    const result = await screen.findByRole("textbox", { name: "Result" });

    // 10 m³ loose × 1.6 t/m³ × 1.03 = 16.48 t
    await waitFor(() => { expect(result).toHaveValue("16.48"); });
    const conversion = api.sent.filter((s) => s.path.endsWith("/conversions")).at(-1);
    expect(conversion?.body).toEqual({ quantity: { value: "10", unit: "m3", state: "loose" }, unit: "t", state: null, on: todayIn("UTC"), moisture: null });

    fireEvent.click(screen.getByRole("button", { name: "Factors used" }));
    expect(screen.getByText("Loose density", { selector: "dt" }).nextSibling).toHaveTextContent("1.6 t/m³");
    expect(screen.getByText("Moisture", { selector: "dt" }).nextSibling).toHaveTextContent("3 %");

    // to m³ in the bank: 16.48 / (2.2 × 1.03) = 7.273
    fireEvent.click(within(screen.getByRole("group", { name: "Unit of Result" })).getByRole("button", { name: "m³ cubic metres" }));
    fireEvent.click(within(screen.getByRole("group", { name: "State of Result" })).getByRole("button", { name: "Bank" }));
    await waitFor(() => { expect(result).toHaveValue("7.273"); });

    // Without a compacted density, that state is not offered.
    expect(within(screen.getByRole("group", { name: "State of Quantity" })).queryByRole("button", { name: "Compacted" })).not.toBeInTheDocument();
  });

  it("has no detectable accessibility problems with the factors open", async () => {
    await renderAt(`/settings/materials/${materialId}`, fakeApi({ permissions: manager, materials: [material()] }));
    const result = await screen.findByRole("textbox", { name: "Result" });
    await waitFor(() => { expect(result).toHaveValue("16.48"); });
    fireEvent.click(screen.getByRole("button", { name: "Factors used" }));

    const results = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});

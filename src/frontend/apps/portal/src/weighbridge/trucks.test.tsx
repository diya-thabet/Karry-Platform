import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, hauler, truck } from "../testing/fakeApi";
import { kgFrom, truckInput } from "./api";

const commercial = ["weighbridge.trucks.read", "weighbridge.trucks.manage", "weighbridge.trucks.verify"];
const weighmaster = ["weighbridge.trucks.read", "weighbridge.trucks.manage"];
const reader = ["weighbridge.trucks.read"];

const unknownTruck = truck({ id: "0192f0c1-0000-7000-8000-000000009602", plate: "777 TU 1", plateKey: "777TU1", tare: null, maxPayloadKg: null, axles: null, status: "unverified" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function choose(label: string | RegExp, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

describe("typed masses and the truck input", () => {
  it("reads whole kg with spaces and refuses decimals, zero and text", () => {
    expect(kgFrom("14 200")).toBe(14200);
    expect(kgFrom("")).toBeNull();
    expect(kgFrom("14,5")).toBeUndefined();
    expect(kgFrom("0")).toBeUndefined();
    expect(kgFrom("lourd")).toBeUndefined();
  });

  it("sends no tare when neither its mass nor its day is given, and no hauler for the empty choice", () => {
    const input = truckInput("123 TU 1", { haulerId: "none", tareKg: "", tareOn: "", tareMethod: "weighed", maxPayloadKg: "26 000", axles: " 2+3 " });
    expect(input).toEqual({ plate: "123 TU 1", haulerId: null, tare: null, maxPayloadKg: 26000, axles: "2+3" });
  });
});

describe("trucks (US-P3-SAL-04)", () => {
  it("finds a truck from settings by any part of its plate, however it is typed", async () => {
    const api = fakeApi({ permissions: reader, trucks: [truck(), truck({ id: "0192f0c1-0000-7000-8000-000000009603", plate: "88 TU 9999", plateKey: "88TU9999" })] });
    await renderAt("/settings", api);

    fireEvent.click(await screen.findByRole("link", { name: "Trucks" }));
    expect(await screen.findByRole("link", { name: "88 TU 9999" })).toBeInTheDocument();

    fireEvent.change(screen.getByRole("searchbox", { name: /^Plate/ }), { target: { value: "tu-4567" } });

    await waitFor(() => { expect(screen.queryByRole("link", { name: "88 TU 9999" })).not.toBeInTheDocument(); });
    const row = screen.getByRole("link", { name: "123 TU 4567" }).closest("tr");
    expect(within(row as HTMLElement).getByText("14,200 kg")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New truck" })).not.toBeInTheDocument();
  });

  it("registers a truck with its hauler, tare and payload", async () => {
    const api = fakeApi({ permissions: commercial, haulers: [hauler()] });
    await renderAt("/settings/trucks/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Plate/ }), { target: { value: "200 tu 3000" } });
    await choose(/^Hauler/, "Transports Ben Salah");
    fireEvent.change(screen.getByRole("textbox", { name: /^Registered tare/ }), { target: { value: "14 250" } });
    fireEvent.change(screen.getByLabelText(/^Tare date/), { target: { value: "2026-11-20" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Legal payload/ }), { target: { value: "26000" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Axles/ }), { target: { value: "2+3" } });
    fireEvent.click(screen.getByRole("button", { name: "Register the truck" }));

    expect(await screen.findByRole("heading", { name: "200 TU 3000" })).toBeInTheDocument();
    const sent = api.sent.find((s) => s.method === "POST" && s.path === "/api/v1/weighbridge/trucks");
    expect(sent?.body).toMatchObject({
      plate: "200 TU 3000", haulerId: hauler().id, tare: { kg: 14250, on: "2026-11-20", method: "weighed" }, maxPayloadKg: 26000, axles: "2+3",
    });
  });

  it("lets a controller verify a truck met at the desk, which closes its flag", async () => {
    const api = fakeApi({ permissions: commercial, trucks: [unknownTruck] });
    await renderAt(`/settings/trucks/${unknownTruck.id}`, api);

    expect(await screen.findByRole("heading", { name: "Truck to verify" })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Verify the truck" }));

    await waitFor(() => { expect(screen.queryByRole("heading", { name: "Truck to verify" })).not.toBeInTheDocument(); });
    expect(api.sent.some((s) => s.method === "POST" && s.path === `/api/v1/weighbridge/trucks/${unknownTruck.id}/verify`)).toBe(true);
  });

  it("does not offer the weighmaster to verify the truck they met", async () => {
    await renderAt(`/settings/trucks/${unknownTruck.id}`, fakeApi({ permissions: weighmaster, trucks: [unknownTruck] }));

    expect(await screen.findByText(/Someone other than the weighmaster verifies it/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Verify the truck" })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save" })).toBeInTheDocument();
  });

  it("archives a truck, which then cannot be changed until it is restored", async () => {
    await renderAt(`/settings/trucks/${truck().id}`, fakeApi({ permissions: commercial, trucks: [truck()] }));

    fireEvent.click(await screen.findByRole("button", { name: "Archive" }));

    expect(await screen.findByText(/This truck is archived/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Restore" }));
    expect(await screen.findByRole("button", { name: "Save" })).toBeInTheDocument();
  });
});

describe("haulers", () => {
  it("adds a hauler and refuses a second one with the same name however it is written", async () => {
    const api = fakeApi({ permissions: commercial, haulers: [hauler()] });
    await renderAt("/settings/haulers", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Name/ }), { target: { value: "Trans Nord" } });
    fireEvent.click(screen.getByRole("button", { name: "Add the hauler" }));
    expect(await screen.findByText("Trans Nord")).toBeInTheDocument();

    fireEvent.change(screen.getByRole("textbox", { name: /^Name/ }), { target: { value: "transports ben-salah" } });
    fireEvent.click(screen.getByRole("button", { name: "Add the hauler" }));
    expect(await screen.findByText("Another hauler of the company already has this name.")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST" && s.path === "/api/v1/weighbridge/haulers")).toHaveLength(2);
  });
});

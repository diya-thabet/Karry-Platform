import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { customer, fakeApi, material, priceList, product, site } from "../testing/fakeApi";
import { priceText } from "./api";

const commercial = ["sales.customers.read", "sales.customers.manage", "sales.products.read", "sales.products.manage", "sales.prices.manage", "materials.catalog.read", "organization.sites.read"];
const reader = ["sales.customers.read", "sales.products.read", "materials.catalog.read", "organization.sites.read"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function choose(label: string | RegExp, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

describe("price text", () => {
  it("keeps typed prices exact and refuses zero, negatives and more than 4 decimals", () => {
    expect(priceText("27,5")).toBe("27.5");
    expect(priceText("12.3456")).toBe("12.3456");
    expect(priceText("12.34567")).toBeNull();
    expect(priceText("0")).toBeNull();
    expect(priceText("-3")).toBeNull();
  });
});

describe("customers (US-P3-SAL-01)", () => {
  it("lists the customers from settings, read-only without the manage permission", async () => {
    await renderAt("/settings", fakeApi({ permissions: reader, customers: [customer(), customer({ id: "0192f0c1-0000-7000-8000-000000007002", code: "CLI-7", legalName: "Sable & Co", status: "blocked", blockedReason: "Impayés" })] }));

    fireEvent.click(await screen.findByRole("link", { name: "Customers" }));

    const row = (await screen.findByRole("link", { name: "Sable & Co" })).closest("tr");
    expect(within(row as HTMLElement).getByText("Blocked")).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New customer" })).not.toBeInTheDocument();
  });

  it("creates a customer with its billing address, contact and credit limit", async () => {
    const api = fakeApi({ permissions: commercial, sites: [site()] });
    await renderAt("/settings/customers/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Code/ }), { target: { value: "cli-1" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Legal name/ }), { target: { value: "Bâtisseurs du Cap" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Credit limit/ }), { target: { value: "50000,50" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Address/ }), { target: { value: "12 rue du Port" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^City/ }), { target: { value: "Bizerte" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Country/ }), { target: { value: "tn" } });
    fireEvent.click(await screen.findByRole("checkbox", { name: "Carrière Nord" }));
    fireEvent.click(screen.getByRole("button", { name: "Create the customer" }));

    expect(await screen.findByRole("heading", { name: "Bâtisseurs du Cap" })).toBeInTheDocument();
    const sent = api.sent.find((s) => s.method === "POST" && s.path === "/api/v1/sales/customers");
    expect(sent?.body).toMatchObject({
      code: "CLI-1", legalName: "Bâtisseurs du Cap", basis: "t", allowedSiteIds: [site().id],
      addresses: [{ kind: "billing", line1: "12 rue du Port", city: "Bizerte", country: "TN" }],
      creditLimit: { amount: "50000.50" },
    });
  });

  it("blocks a customer only with a reason, and unblocks it", async () => {
    const api = fakeApi({ permissions: commercial, customers: [customer()] });
    await renderAt(`/settings/customers/${customer().id}`, api);

    fireEvent.click(await screen.findByRole("button", { name: "Block the customer" }));
    expect(await screen.findByText("Give the reason the weighmaster will read.")).toBeInTheDocument();
    expect(api.sent.some((s) => s.path.endsWith("/block"))).toBe(false);

    fireEvent.change(screen.getByRole("textbox", { name: /^Reason/ }), { target: { value: "Factures impayées" } });
    fireEvent.click(screen.getByRole("button", { name: "Block the customer" }));
    expect(await screen.findByText("Blocked: Factures impayées")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Unblock the customer" }));
    await waitFor(() => { expect(screen.queryByText("Blocked: Factures impayées")).not.toBeInTheDocument(); });
  });

  it("shows a masked credit limit as hidden and never sends it (TC-P3-SAL-003)", async () => {
    const masked = customer({ creditLimit: null, maskedFields: ["creditLimit"] });
    const api = fakeApi({ permissions: commercial, customers: [masked] });
    await renderAt(`/settings/customers/${masked.id}`, api);

    expect(await screen.findByLabelText("Hidden by the company’s policy")).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: /^Credit limit/ })).not.toBeInTheDocument();
    fireEvent.change(screen.getByRole("textbox", { name: /^Legal name/ }), { target: { value: "Cap SA" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => { expect(api.sent.some((s) => s.method === "PUT")).toBe(true); });
    expect(api.sent.find((s) => s.method === "PUT")?.body).toMatchObject({ legalName: "Cap SA", creditLimit: null });
  });
});

describe("products (US-P3-SAL-02 AC1)", () => {
  it("creates a product from a sold fraction, raw rock not offered", async () => {
    const rock = material({ id: "0192f0c1-0000-7000-8000-000000006002", code: "ROC", name: "Roche", kind: "raw-rock" });
    const api = fakeApi({ permissions: commercial, materials: [material(), rock] });
    await renderAt("/settings/products/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Code/ }), { target: { value: "g-0/31.5" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Name/ }), { target: { value: "Grave" } });
    fireEvent.click(await screen.findByRole("combobox", { name: /^Material/ }));
    expect(screen.queryByRole("option", { name: "ROC — Roche" })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole("option", { name: "0/31.5 — Grave 0/31.5" }));
    await choose(/^Sale unit/, "m³ loose");
    fireEvent.click(screen.getByRole("button", { name: "Create the product" }));

    expect(await screen.findByRole("heading", { name: "Grave" })).toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "POST" && s.path === "/api/v1/sales/products")?.body)
      .toEqual({ code: "G-0/31.5", name: "Grave", materialId: material().id, saleUnit: "m3-loose" });
  });
});

describe("price lists (US-P3-SAL-02 AC2)", () => {
  it("lists the site's versions, hides prices the role may not read, and withdraws a scheduled version", async () => {
    const scheduled = priceList({ id: "0192f0c1-0000-7000-8000-000000009002", effectiveFrom: "2027-01-01", inForce: false, lines: [{ productId: product().id, unitPrice: 33 }] });
    const masked = priceList({ id: "0192f0c1-0000-7000-8000-000000009003", customerId: customer().id, lines: [{ productId: product().id, maskedFields: ["unitPrice"] }] });
    const api = fakeApi({ permissions: commercial, sites: [site()], products: [product()], customers: [customer()], priceLists: [scheduled, priceList(), masked] });
    await renderAt("/settings/price-lists", api);

    const table = await screen.findByRole("table", { name: "Versions" });
    await waitFor(() => { expect(within(table).getAllByRole("row")).toHaveLength(4); });
    expect(within(table).getAllByText("In force")).toHaveLength(2);
    expect(within(table).getByText("Scheduled")).toBeInTheDocument();
    expect(within(table).getByLabelText("Hidden by the company’s policy")).toBeInTheDocument();
    expect(within(table).getByText("Bâtisseurs du Cap")).toBeInTheDocument();

    fireEvent.click(within(table).getByRole("button", { name: "Withdraw" }));
    await waitFor(() => { expect(api.sent.some((s) => s.path === `/api/v1/sales/price-lists/${scheduled.id}/withdraw`)).toBe(true); });
  });

  it("creates a version with the prices typed, leaving empty products out", async () => {
    const sand = product({ id: "0192f0c1-0000-7000-8000-000000008002", code: "SAB-0/4", name: "Sable" });
    const api = fakeApi({ permissions: commercial, sites: [site()], products: [product(), sand] });
    await renderAt("/settings/price-lists", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Currency/ }), { target: { value: "tnd" } });
    fireEvent.change(await screen.findByRole("textbox", { name: /^G-8\/16/ }), { target: { value: "30,250" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the version" }));

    await waitFor(() => { expect(api.sent.some((s) => s.method === "POST" && s.path === "/api/v1/sales/price-lists")).toBe(true); });
    expect(api.sent.find((s) => s.method === "POST" && s.path === "/api/v1/sales/price-lists")?.body).toMatchObject({
      siteId: site().id, customerId: null, currency: "TND", lines: [{ productId: product().id, unitPrice: "30.250" }],
    });
  });

  it("previews the price on a date, and says when there is none", async () => {
    const sand = product({ id: "0192f0c1-0000-7000-8000-000000008002", code: "SAB-0/4", name: "Sable" });
    const api = fakeApi({
      permissions: commercial, sites: [site()], products: [product(), sand],
      quotes: { [product().id]: { productId: product().id, unitPrice: 27.5, currency: "TND", saleUnit: "t", priceListId: priceList().id, effectiveFrom: "2026-11-01", source: "customer" } },
    });
    await renderAt("/settings/price-lists", api);

    await choose(/^Product/, "G-8/16 — Gravier 8/16");
    fireEvent.click(screen.getByRole("button", { name: "Show the price" }));
    expect(await screen.findByText(/from the customer’s list of/)).toBeInTheDocument();

    await choose(/^Product/, "SAB-0/4 — Sable");
    fireEvent.click(screen.getByRole("button", { name: "Show the price" }));
    expect(await screen.findByText(/No price: no list in force prices this product here/)).toBeInTheDocument();
  });
});

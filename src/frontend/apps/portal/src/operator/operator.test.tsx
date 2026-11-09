import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, operatorTenant, subscriptionOf } from "../testing/fakeApi";
import { actionsFor, isOperator, slugFrom, slugValid } from "./api";
import { findTable } from "../testing/tables";

const nord = operatorTenant();
const sud = operatorTenant({
  id: "0192f0c1-0000-7000-8000-0000000c0002", slug: "carriere-sud", name: "Carrière Sud", planCode: "business", status: "past_due",
  mrr: 1980, sites: 2, assets: 40, officeUsers: 12,
});
const atlas = operatorTenant({
  id: "0192f0c1-0000-7000-8000-0000000c0003", slug: "atlas-mines", name: "Atlas Mines", planCode: "enterprise", status: "active",
  mrr: null, mrrCurrency: null, sites: null, assets: null, officeUsers: null, lastActivity: null, complete: false,
});
const tenantPaths = ["/api/v1/tenancy/current", "/api/v1/billing/entitlements", "/api/v1/flags", "/api/v1/identity/me/access"];

async function renderAs(path: string, api: ReturnType<typeof fakeApi>, options: { operator?: boolean; language?: "fr" | "en"; pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: options.language ?? "en", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth(options.operator === false ? {} : { platformRole: "operator", givenName: "Ops", familyName: "Karry" });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("operator console (US-P1-OPS-03 AC1)", () => {
  it("lists every company with its plan, subscription, size, MRR and activity, and the MRR total", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord, sud, atlas] });
    await renderAs("/operator", api);

    const table = await findTable("Companies");
    const rows = await within(table).findAllByRole("row");
    expect(rows.slice(1).map((row) => within(row).getAllByRole("cell")[0]?.textContent)).toEqual(["Atlas Minesatlas-mines", "Carrière Nordcarriere-nord", "Carrière Sudcarriere-sud"]);
    const south = within(table).getByRole("row", { name: /Carrière Sud/ });
    expect(within(south).getByText("business v1")).toBeInTheDocument();
    expect(within(south).getByText("Payment overdue")).toBeInTheDocument();
    expect(within(south).getByText("40")).toBeInTheDocument();
    expect(within(south).getByText("€1,980.00")).toBeInTheDocument();
    const atlasRow = within(table).getByRole("row", { name: /Atlas Mines/ });
    expect(within(atlasRow).getByText("Incomplete")).toBeInTheDocument();
    expect(within(atlasRow).getByText("Quoted")).toBeInTheDocument();
    expect(within(within(table).getByRole("row", { name: /Carrière Nord/ })).getByText("Trial")).toBeInTheDocument();
    expect(screen.getByText("€1,980.00", { selector: "span.text-2xl" })).toBeInTheDocument();
  });

  it("asks nothing of a company's portal for an operator, and lands on the console", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs("/", api);

    expect(await findTable("Companies")).toBeInTheDocument();
    expect(screen.getByText("Operator console")).toBeInTheDocument();
    expect(api.requested.filter((path) => tenantPaths.includes(path))).toEqual([]);
  });

  it("asks an operator signed in without a second factor to sign in again, then loads the console (ADR-0029 §5)", async () => {
    const i18n = await createI18n({ language: "en" });
    const auth = await signedInAuth({ platformRole: "operator", givenName: "Ops", familyName: "Karry" });
    const api = fakeApi({ permissions: [], operatorTenants: [nord], operatorWithoutMfa: () => auth.stepUps === 0 });
    render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/operator"] })} />);

    const dialog = await screen.findByRole("dialog", undefined, { timeout: 4000 }); // after the query's one retry
    expect(within(dialog).getByText(/must sign in with their second factor/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Sign in again" }));

    expect(await screen.findByRole("link", { name: "Carrière Nord" })).toBeInTheDocument();
    expect(auth.stepUps).toBe(1);
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("is not for a company's people: they stay in their portal", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs("/operator", api, { operator: false });

    expect(await screen.findByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Companies" })).not.toBeInTheDocument();
    expect(api.requested).not.toContain("/api/v1/tenancy/tenants");
  });

  it("creates a company with an address from its name and invites its first administrator", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs("/operator", api);
    fireEvent.click(await screen.findByRole("button", { name: "New company" }));
    const dialog = await screen.findByRole("dialog", { name: "New company" });

    fireEvent.change(within(dialog).getByLabelText("Company name"), { target: { value: "Carrière de Sfax" } });
    expect(within(dialog).getByLabelText(/^Address/)).toHaveValue("carriere-de-sfax");
    fireEvent.change(within(dialog).getByLabelText("First name"), { target: { value: "Amel" } });
    fireEvent.change(within(dialog).getByLabelText("Last name"), { target: { value: "Admin" } });
    fireEvent.change(within(dialog).getByLabelText(/^E-mail/), { target: { value: "amel@sfax.test" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the company" }));

    expect(await screen.findByText("Carrière de Sfax created")).toBeInTheDocument();
    expect(screen.getByText("Invitation sent to amel@sfax.test.")).toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "POST" && s.path === "/api/v1/tenancy/tenants")?.body).toEqual({
      slug: "carriere-de-sfax", name: "Carrière de Sfax", admin: { firstName: "Amel", lastName: "Admin", email: "amel@sfax.test" },
    });
    expect(await screen.findByRole("row", { name: /Carrière de Sfax/ })).toBeInTheDocument();
    expect(screen.queryByRole("dialog", { name: "New company" })).not.toBeInTheDocument();
  });

  it("creates a company without an administrator and warns when an invitation could not be sent", async () => {
    const api = fakeApi({ permissions: [], invitationDelivered: false });
    await renderAs("/operator", api);
    fireEvent.click(await screen.findByRole("button", { name: "New company" }));
    let dialog = await screen.findByRole("dialog", { name: "New company" });
    fireEvent.change(within(dialog).getByLabelText("Company name"), { target: { value: "Gafsa" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the company" }));
    expect(await screen.findByText("Gafsa created")).toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "POST")?.body).toEqual({ slug: "gafsa", name: "Gafsa", admin: null });

    fireEvent.click(screen.getByRole("button", { name: "New company" }));
    dialog = await screen.findByRole("dialog", { name: "New company" });
    fireEvent.change(within(dialog).getByLabelText("Company name"), { target: { value: "Kef" } });
    fireEvent.change(within(dialog).getByLabelText(/^Address/), { target: { value: "kef-carriere" } });
    fireEvent.change(within(dialog).getByLabelText("First name"), { target: { value: "Sami" } });
    fireEvent.change(within(dialog).getByLabelText("Last name"), { target: { value: "Admin" } });
    fireEvent.change(within(dialog).getByLabelText(/^E-mail/), { target: { value: "sami@kef.test" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the company" }));
    expect(await screen.findByText("The invitation to sami@kef.test could not be sent: send it again from the company later.")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST").at(-1)?.body).toMatchObject({ slug: "kef-carriere" });
  });

  it("refuses a missing name, a bad address and half an administrator before asking the API", async () => {
    const api = fakeApi({ permissions: [] });
    await renderAs("/operator", api);
    fireEvent.click(await screen.findByRole("button", { name: "New company" }));
    const dialog = await screen.findByRole("dialog", { name: "New company" });

    fireEvent.change(within(dialog).getByLabelText(/^Address/), { target: { value: "-x" } });
    fireEvent.change(within(dialog).getByLabelText(/^E-mail/), { target: { value: "not-an-address" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the company" }));

    expect(await within(dialog).findByText("Give the company a name.")).toBeInTheDocument();
    expect(within(dialog).getByText(/3 to 40 lowercase letters/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Give the administrator's first name/)).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);

    fireEvent.change(within(dialog).getByLabelText("Company name"), { target: { value: "Gafsa" } });
    fireEvent.change(within(dialog).getByLabelText(/^Address/), { target: { value: "gafsa" } });
    fireEvent.change(within(dialog).getByLabelText(/^E-mail/), { target: { value: "sami@gafsa.test" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the company" }));
    await new Promise((settled) => setTimeout(settled, 100)); // a request would have left by now
    expect(within(dialog).getByText(/Give the administrator's first name/)).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);
  });

  it("suggests addresses the API accepts", () => {
    expect(slugFrom("Carrière de Sfax")).toBe("carriere-de-sfax");
    expect(slugFrom("  Société -- Mines & Co. ")).toBe("societe-mines-co");
    expect(slugFrom("x".repeat(45))).toHaveLength(40);
    expect(["abc", "carriere-sfax", "a1b"].every(slugValid)).toBe(true);
    expect(["ab", "-abc", "abc-", "a--b", "Abc", "a_b", "x".repeat(41)].some(slugValid)).toBe(false);
  });

  it("knows an operator only without a company in the token", () => {
    expect(isOperator({ subject: "s", name: "n", email: undefined, tenantId: undefined, platformRole: "operator" })).toBe(true);
    expect(isOperator({ subject: "s", name: "n", email: undefined, tenantId: "t", platformRole: "operator" })).toBe(false);
    expect(isOperator({ subject: "s", name: "n", email: undefined, tenantId: undefined, platformRole: undefined })).toBe(false);
  });

  it("is in French", async () => {
    await renderAs("/operator", fakeApi({ permissions: [], operatorTenants: [nord, atlas] }), { language: "fr" });
    expect(await findTable("Entreprises")).toBeInTheDocument();
    expect(screen.getByText("Sur devis")).toBeInTheDocument();
  });

  it("has no hard-coded text (TC-P1-OFF-007)", async () => {
    await renderAs("/operator", fakeApi({ permissions: [], operatorTenants: [nord, sud, atlas] }), { pseudo: true });
    await vi.waitFor(() => { expect(document.querySelectorAll("tbody tr:not([aria-hidden])")).toHaveLength(3); });
    await vi.waitFor(() => { expect(document.querySelector("table[aria-busy]")).toBeNull(); }); // three skeleton rows come first

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], time, script, style, option, select") !== null;
      // Data shown as it is: counts, amounts, the dash of an unknown count.
      const data = /^(—|[\d\s.,€]+|.*\d.*€.*|€.*\d.*)$/.test(text);
      if (text !== "" && !hidden && !data && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });

  it("has no serious accessibility violation, with the new company form open", async () => {
    await renderAs("/operator", fakeApi({ permissions: [], operatorTenants: [nord, sud] }));
    fireEvent.click(await screen.findByRole("button", { name: "New company" }));
    await screen.findByRole("dialog", { name: "New company" });

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

const nordPage = `/operator/companies/${nord.id}`;
const invoice = {
  id: "0192f0c1-0000-7000-8000-00000000f001", number: "K-2026-001", amount: 590, currency: "EUR", issuedOn: "2026-10-01", dueOn: "2026-10-15", paidOn: null,
};

async function choose(label: string, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

describe("operator console: one company (US-P1-OPS-03 AC1, US-P1-BIL-02)", () => {
  it("opens a company from the list with its subscription and invoices", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord, sud], tenantInvoices: { [nord.id]: [invoice] } });
    await renderAs("/operator", api);
    fireEvent.click(await screen.findByRole("link", { name: "Carrière Nord" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Carrière Nord" })).toBeInTheDocument();
    expect(await screen.findByText("Trial ends")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Plan" })).toHaveTextContent("professional v1");
    expect(screen.getByLabelText("Sites")).toHaveValue("1");
    const invoices = await findTable("Invoices");
    expect(within(invoices).getByText("K-2026-001")).toBeInTheDocument();
    expect(within(invoices).getByText("€590.00")).toBeInTheDocument();
    expect(api.requested).toContain(`/api/v1/billing/tenants/${nord.id}/subscription`);
    fireEvent.click(within(screen.getByRole("navigation", { name: "Breadcrumb" })).getByRole("link", { name: "Companies" }));
    expect(await findTable("Companies")).toBeInTheDocument();
  });

  it("offers only the moves the subscription can make, and activates a trial", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs(nordPage, api);
    await screen.findByText("Trial ends");

    expect(screen.queryByRole("button", { name: "Record a missed payment" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Cancel the subscription" })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Activate" }));

    expect(await screen.findByText("Subscription now Active")).toBeInTheDocument();
    expect(api.sent.find((s) => s.path.endsWith("/subscription/actions"))?.body).toEqual({ action: "activate" });
    expect(await screen.findByRole("button", { name: "Record a missed payment" })).toBeInTheDocument();
    expect(screen.queryByText("Trial ends")).not.toBeInTheDocument();
  });

  it("records a missed payment", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], tenantSubscriptions: { [nord.id]: subscriptionOf({ status: "active", trialEndsAt: null }) } });
    await renderAs(nordPage, api);

    fireEvent.click(await screen.findByRole("button", { name: "Record a missed payment" }));
    expect(await screen.findByText("Unpaid since")).toBeInTheDocument();
    expect(await screen.findByRole("button", { name: "Record the payment" })).toBeInTheDocument();
    expect(api.sent.find((s) => s.path.endsWith("/subscription/actions"))?.body).toEqual({ action: "missed-payment" });
  });

  it("cancels only after confirmation, and then the terms are closed", async () => {
    const api2 = fakeApi({ permissions: [], operatorTenants: [nord], tenantSubscriptions: { [nord.id]: subscriptionOf({ status: "suspended", trialEndsAt: null }) } });
    await renderAs(nordPage, api2);
    fireEvent.click(await screen.findByRole("button", { name: "Cancel the subscription" }));
    const dialog = await screen.findByRole("dialog", { name: "Cancel this subscription?" });
    expect(api2.sent.filter((s) => s.path.endsWith("/subscription/actions"))).toEqual([]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Cancel the subscription" }));

    expect(await screen.findByText("Subscription now Cancelled")).toBeInTheDocument();
    expect(api2.sent.find((s) => s.path.endsWith("/subscription/actions"))?.body).toEqual({ action: "cancel" });
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Save the terms" })).toBeDisabled();
    expect(screen.getAllByText("This subscription is closed: its terms can no longer change.").length).toBeGreaterThan(0);
  });

  it("changes plan, period and quantities and keeps the rest of the terms", async () => {
    const terms = subscriptionOf({ status: "active", addOns: { "weighbridge-integration": 1 }, extraFeatures: ["fuel"], limitOverrides: { users: 40 } });
    const api = fakeApi({ permissions: [], operatorTenants: [nord], tenantSubscriptions: { [nord.id]: terms } });
    await renderAs(nordPage, api);

    await choose("Plan", "business");
    expect(screen.getByText("The company moves to the latest version of this plan.")).toBeInTheDocument();
    await choose("Period", "Annual");
    fireEvent.change(screen.getByLabelText("Sites"), { target: { value: "3" } });
    fireEvent.change(screen.getByLabelText("Extra machines"), { target: { value: "12" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the terms" }));

    expect(await screen.findByText("Terms saved")).toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "PUT")?.body).toEqual({
      planCode: "business", planVersion: null, period: "annual", sites: 3, extraAssets: 12,
      addOns: { "weighbridge-integration": 1 }, extraFeatures: ["fuel"], limitOverrides: { users: 40 },
    });
  });

  it("never saves terms over a change made by another operator meanwhile, and keeps the operator's entries (H-C1)", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], tenantSubscriptions: { [nord.id]: subscriptionOf({ status: "active" }) } });
    await renderAs(nordPage, api);
    const sites = await screen.findByLabelText("Sites");
    api.changeSubscriptionElsewhere(nord.id, { sites: 2 });

    fireEvent.change(sites, { target: { value: "4" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the terms" }));

    const notice = await screen.findByRole("alert");
    expect(notice).toHaveTextContent("Someone else saved a change here while you were editing.");
    expect(sites).toHaveValue("4");

    fireEvent.click(within(notice).getByRole("button", { name: "Save my changes over theirs" }));
    expect(await screen.findByText("Terms saved")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "PUT").at(-1)?.body).toMatchObject({ sites: 4 });
  });

  it("keeps the operator's typing through a state action on the same page (finding C-2)", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], tenantSubscriptions: { [nord.id]: subscriptionOf({ status: "active", trialEndsAt: null }) } });
    await renderAs(nordPage, api);
    fireEvent.change(await screen.findByLabelText("Sites"), { target: { value: "4" } });

    fireEvent.click(screen.getByRole("button", { name: "Record a missed payment" }));
    expect(await screen.findByText("Unpaid since")).toBeInTheDocument();

    expect(screen.getByLabelText("Sites")).toHaveValue("4");
    fireEvent.click(screen.getByRole("button", { name: "Save the terms" }));
    expect(await screen.findByText("Terms saved")).toBeInTheDocument();
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("keeps the plan version when the plan stays, and refuses quantities that are not whole", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], tenantSubscriptions: { [nord.id]: subscriptionOf({ planVersion: 2 }) } });
    await renderAs(nordPage, api);

    fireEvent.change(await screen.findByLabelText("Sites"), { target: { value: "0" } });
    fireEvent.change(screen.getByLabelText("Extra machines"), { target: { value: "1.5" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the terms" }));
    expect(await screen.findByText("At least 1 site.")).toBeInTheDocument();
    expect(screen.getByText("A whole number, 0 or more.")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "PUT")).toEqual([]);

    fireEvent.change(screen.getByLabelText("Sites"), { target: { value: "1" } });
    fireEvent.change(screen.getByLabelText("Extra machines"), { target: { value: "0" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the terms" }));
    expect(await screen.findByText("Terms saved")).toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "PUT")?.body).toMatchObject({ planCode: "professional", planVersion: 2, sites: 1, extraAssets: 0 });
  });

  it("records an invoice and its payment", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs(nordPage, api);
    fireEvent.click(await screen.findByRole("button", { name: "Record an invoice" }));
    const dialog = await screen.findByRole("dialog", { name: "Record an invoice" });

    fireEvent.change(within(dialog).getByLabelText("Number"), { target: { value: " K-2026-002 " } });
    fireEvent.change(within(dialog).getByLabelText("Amount"), { target: { value: "590,50" } });
    fireEvent.change(within(dialog).getByLabelText("Issued"), { target: { value: "2026-10-01" } });
    fireEvent.change(within(dialog).getByLabelText("Due"), { target: { value: "2026-10-31" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Record" }));

    expect(await screen.findByText("Invoice recorded")).toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "POST" && s.path.endsWith("/invoices"))?.body).toEqual({ number: "K-2026-002", amount: "590.50", currency: "EUR", issuedOn: "2026-10-01", dueOn: "2026-10-31" });
    const invoices = screen.getByRole("table", { name: "Invoices" });
    fireEvent.click(await within(invoices).findByRole("button", { name: "Mark invoice K-2026-002 paid" }));

    expect(await screen.findByText("Payment recorded")).toBeInTheDocument();
    expect(api.sent.find((s) => s.path.endsWith("/payment"))?.body).toEqual({ paidOn: expect.stringMatching(/^\d{4}-\d{2}-\d{2}$/) as unknown });
    expect(await within(invoices).findByText(/^Paid /)).toBeInTheDocument();
  });

  it("refuses an invoice without number, with a bad amount or due before issue, and explains a number used twice", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], tenantInvoices: { [nord.id]: [invoice] } });
    await renderAs(nordPage, api);
    fireEvent.click(await screen.findByRole("button", { name: "Record an invoice" }));
    const dialog = await screen.findByRole("dialog", { name: "Record an invoice" });

    fireEvent.change(within(dialog).getByLabelText("Amount"), { target: { value: "0" } });
    fireEvent.change(within(dialog).getByLabelText("Issued"), { target: { value: "2026-10-10" } });
    fireEvent.change(within(dialog).getByLabelText("Due"), { target: { value: "2026-10-09" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Record" }));
    expect(await within(dialog).findByText("Give the invoice number.")).toBeInTheDocument();
    expect(within(dialog).getByText(/An amount above 0/)).toBeInTheDocument();
    expect(within(dialog).getByText("The due date cannot be before the issue date.")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);

    fireEvent.change(within(dialog).getByLabelText("Number"), { target: { value: "K-2026-001" } });
    fireEvent.change(within(dialog).getByLabelText("Amount"), { target: { value: "ten" } });
    fireEvent.change(within(dialog).getByLabelText("Due"), { target: { value: "2026-10-10" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Record" }));
    await new Promise((settled) => setTimeout(settled, 100)); // a request would have left by now
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);

    fireEvent.change(within(dialog).getByLabelText("Amount"), { target: { value: "10" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Record" }));
    expect(await screen.findByText("An invoice with this number is already recorded.")).toBeInTheDocument();
  });

  it("matches the subscription's transitions", () => {
    expect(actionsFor).toEqual({
      trial: ["activate"], expired: ["activate"], active: ["missed-payment", "cancel"], past_due: ["payment"], restricted: ["payment"],
      suspended: ["payment", "cancel"], cancelled: [], deleted: [],
    });
  });

  it("has no hard-coded text and no serious accessibility violation", async () => {
    await renderAs(nordPage, fakeApi({ permissions: [], operatorTenants: [nord], tenantInvoices: { [nord.id]: [invoice] } }), { pseudo: true });
    await vi.waitFor(() => { expect(document.querySelector("table[aria-busy]")).toBeNull(); }); // skeleton rows first
    await vi.waitFor(() => { expect(document.querySelectorAll("tbody tr")).toHaveLength(1); });
    await vi.waitFor(() => { expect(document.querySelectorAll("dl div").length).toBeGreaterThan(2); });

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], time, script, style, option, select") !== null;
      const data = /^[a-z]+ v\d+$/.test(text); // a plan code and version, shown as it is
      if (text !== "" && !hidden && !data && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, operatorTenant } from "../testing/fakeApi";
import { isOperator, slugFrom, slugValid } from "./api";

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

    const table = await screen.findByRole("table", { name: "Companies" });
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

    expect(await screen.findByRole("table", { name: "Companies" })).toBeInTheDocument();
    expect(screen.getByText("Operator console")).toBeInTheDocument();
    expect(api.requested.filter((path) => tenantPaths.includes(path))).toEqual([]);
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
    expect(await screen.findByRole("table", { name: "Entreprises" })).toBeInTheDocument();
    expect(screen.getByText("Sur devis")).toBeInTheDocument();
  });

  it("has no hard-coded text (TC-P1-OFF-007)", async () => {
    await renderAs("/operator", fakeApi({ permissions: [], operatorTenants: [nord, sud, atlas] }), { pseudo: true });
    await vi.waitFor(() => { expect(document.querySelectorAll("tbody tr")).toHaveLength(3); });

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

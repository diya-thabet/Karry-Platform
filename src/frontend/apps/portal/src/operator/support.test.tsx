import { createMemoryHistory } from "@tanstack/react-router";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, operatorTenant, supportSession } from "../testing/fakeApi";
import { findTable } from "../testing/tables";

const nord = operatorTenant();
const nordPage = `/operator/companies/${nord.id}`;
const code = "4HQX-2M7D-9RKA-TP3B";
const reason = "INC-42: weighbridge totals wrong at night since the update";

async function renderAs(path: string, api: ReturnType<typeof fakeApi>, options: { language?: "fr" | "en"; pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: options.language ?? "en", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth({ platformRole: "operator", givenName: "Ops", familyName: "Karry" });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function openDialog() {
  const card = (await screen.findByRole("heading", { name: "Support access" })).closest("section") ?? document.body;
  fireEvent.click(within(card).getByRole("button", { name: "Open support" }));
  return screen.findByRole("dialog", { name: "Open support" });
}

async function choose(label: string, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

/** Opens a session with the company's code and waits for the company's portal under the banner. */
async function visitWithCode(api: ReturnType<typeof fakeApi>) {
  await renderAs(nordPage, api);
  const dialog = await openDialog();
  fireEvent.change(within(dialog).getByRole("textbox", { name: "Support code" }), { target: { value: code } });
  fireEvent.click(within(dialog).getByRole("button", { name: "Open support" }));
  return screen.findByRole("complementary", { name: "Karry support session" });
}

const withHeader = (api: ReturnType<typeof fakeApi>) => api.supportCalls.filter((call) => call.header !== null);

describe("support access from the operator console (US-P1-OPS-03 AC3)", () => {
  afterEach(() => {
    vi.useRealTimers();
    sessionStorage.clear();
  });

  it("opens the company's portal with its code, every call naming the session, under a banner", async () => {
    const api = fakeApi({ permissions: ["organization.sites.read"], operatorTenants: [nord] });
    await renderAs(nordPage, api);
    const dialog = await openDialog();

    fireEvent.change(within(dialog).getByRole("textbox", { name: "Support code" }), { target: { value: " 4hqx-2m7d-9rka-tp3b " } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Open support" }));

    const banner = await screen.findByRole("complementary", { name: "Karry support session" });
    expect(within(banner).getByText(/^Karry support in Carrière Nord: read only, ends at /)).toBeInTheDocument();
    expect(within(banner).getByRole("button", { name: "End support" })).toBeInTheDocument();
    expect(await screen.findByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([{ method: "POST", path: `/tenants/${nord.id}/support-sessions`, body: { code, reason: null } }]);
    await vi.waitFor(() => { expect(withHeader(api).map((call) => call.path)).toContain("/api/v1/identity/me/access"); });
    const opened = api.supportCalls.find((call) => call.path === `/api/v1/identity/tenants/${nord.id}/support-sessions` && call.method === "POST");
    expect(opened?.header).toBeNull();
    expect(new Set(withHeader(api).map((call) => call.header))).toEqual(new Set([`${nord.id}.0192f0c1-0000-7000-8000-00000000d101`]));
    expect(withHeader(api).filter((call) => call.path.startsWith("/api/v1/tenancy/tenants"))).toEqual([]);
    // Kept for a reload of this tab.
    expect(JSON.parse(sessionStorage.getItem("karry.support-visit") ?? "null")).toMatchObject({ tenantId: nord.id, sessionId: "0192f0c1-0000-7000-8000-00000000d101" });
  });

  it("asks for the code, and explains a wrong one", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs(nordPage, api);
    const dialog = await openDialog();

    fireEvent.click(within(dialog).getByRole("button", { name: "Open support" }));
    expect(await within(dialog).findByText("Enter the code the company gave you.")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);

    fireEvent.change(within(dialog).getByRole("textbox", { name: "Support code" }), { target: { value: "AAAA-BBBB-CCCC-DDDD" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Open support" }));
    expect(await screen.findByText("This support code is not valid, has expired or was already used. Ask the company for a new one.")).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: "Karry support session" })).not.toBeInTheDocument();
  });

  it("opens emergency access only with a written reason, after the warning", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await renderAs(nordPage, api);
    const dialog = await openDialog();
    await choose("How", "Emergency access");

    expect(within(dialog).getByRole("note")).toHaveTextContent("e-mails every administrator of the company");
    const reasonBox = within(dialog).getByRole("textbox", { name: "Reason" });
    fireEvent.change(reasonBox, { target: { value: "   too short   " } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Open emergency access" }));
    expect(await within(dialog).findByText("Write between 30 and 500 characters.")).toBeInTheDocument();
    expect(within(dialog).getByText(/\(9 so far\)/)).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);

    fireEvent.change(reasonBox, { target: { value: `  ${reason}  ` } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Open emergency access" }));

    expect(await screen.findByRole("complementary", { name: "Karry support session" })).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST").map((s) => s.body)).toEqual([{ code: null, reason }]);
  });

  it("ends the visit with End support and comes back to the company's page", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await visitWithCode(api);

    fireEvent.click(screen.getByRole("button", { name: "End support" }));

    expect(await screen.findByText("Support session ended")).toBeInTheDocument();
    expect(await screen.findByRole("heading", { level: 1, name: "Carrière Nord" })).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: "Karry support session" })).not.toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST").map((s) => s.path)).toEqual([
      `/tenants/${nord.id}/support-sessions`, `/tenants/${nord.id}/support-sessions/0192f0c1-0000-7000-8000-00000000d101/end`,
    ]);
    expect(await screen.findByText("Over")).toBeInTheDocument();
    expect(sessionStorage.length).toBe(0);
  });

  it("leaves at once when the company ends the session", async () => {
    const api = fakeApi({ permissions: ["organization.sites.read"], operatorTenants: [nord] });
    await visitWithCode(api);
    api.endSupport("0192f0c1-0000-7000-8000-00000000d101");

    fireEvent.click(await screen.findByRole("link", { name: "Sites" }));

    expect(await screen.findByText("The support session has ended: the company may have ended it")).toBeInTheDocument();
    expect(await screen.findByRole("heading", { level: 1, name: "Carrière Nord" })).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: "Karry support session" })).not.toBeInTheDocument();
  });

  it("leaves by itself when the hour is over", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    await visitWithCode(api);

    act(() => { vi.advanceTimersByTime(60 * 60_000); });

    expect(await screen.findByText("The support session's hour is over")).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: "Karry support session" })).not.toBeInTheDocument();
  });

  it("keeps the visit through a reload of this tab, but not past its end", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], supportSessions: [supportSession({ tenantId: nord.id, operatorName: "Ops Karry" })] });
    const visit = { tenantId: nord.id, tenantName: nord.name, sessionId: "0192f0c1-0000-7000-8000-00000000d001" };
    sessionStorage.setItem("karry.support-visit", JSON.stringify({ ...visit, endsAt: new Date(Date.now() + 10 * 60_000).toISOString() }));
    await renderAs("/", api);
    expect(await screen.findByRole("complementary", { name: "Karry support session" })).toBeInTheDocument();
  });

  it("does not resume a visit whose hour is over", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord] });
    sessionStorage.setItem("karry.support-visit", JSON.stringify({ tenantId: nord.id, tenantName: nord.name, sessionId: "x", endsAt: new Date(Date.now() - 1000).toISOString() }));
    await renderAs("/", api);

    expect(await findTable("Companies")).toBeInTheDocument();
    expect(screen.queryByRole("complementary", { name: "Karry support session" })).not.toBeInTheDocument();
  });

  it("lists the company's sessions and continues only the operator's own running one", async () => {
    const mine = supportSession({ tenantId: nord.id, operatorName: "Ops Karry", endsAt: new Date(Date.now() + 30 * 60_000).toISOString() });
    const theirs = supportSession({ id: "0192f0c1-0000-7000-8000-00000000d002", tenantId: nord.id, operatorName: "Anna Support", mode: "break_glass", reason });
    const api = fakeApi({ permissions: [], operatorTenants: [nord], supportSessions: [mine, theirs] });
    await renderAs(nordPage, api);

    const table = await findTable("Support sessions");
    const other = (await within(table).findByText("Anna Support")).closest("tr") as HTMLElement;
    expect(within(other).getByText(reason)).toBeInTheDocument();
    expect(within(other).queryByRole("button")).not.toBeInTheDocument();
    fireEvent.click(within(table).getByRole("button", { name: /Continue the session started/ }));

    expect(await screen.findByRole("complementary", { name: "Karry support session" })).toBeInTheDocument();
    await vi.waitFor(() => { expect(withHeader(api).map((call) => call.header)).toContain(`${nord.id}.${mine.id}`); });
  });

  it("is in French", async () => {
    await renderAs(nordPage, fakeApi({ permissions: [], operatorTenants: [nord] }), { language: "fr" });
    const card = (await screen.findByRole("heading", { name: "Accès support" })).closest("section") ?? document.body;
    fireEvent.click(within(card).getByRole("button", { name: "Ouvrir le support" }));
    expect(await screen.findByRole("textbox", { name: "Code support" })).toBeInTheDocument();
  });

  it("has no hard-coded text and no serious accessibility violation in the dialog", async () => {
    const api = fakeApi({ permissions: [], operatorTenants: [nord], supportSessions: [supportSession({ tenantId: nord.id, active: false })] });
    await renderAs(nordPage, api, { pseudo: true });
    await screen.findByText("Anna Support");
    const card = screen.getAllByRole("table").at(-1)?.closest("section") ?? document.body;
    const [open] = within(card).getAllByRole("button");
    expect(open).toBeDefined();
    if (open) {
      fireEvent.click(open);
    }

    await screen.findByRole("dialog");

    const untranslated = (root: Element) => {
      const found: string[] = [];
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let node = walker.nextNode(); node; node = walker.nextNode()) {
        const text = node.textContent?.trim() ?? "";
        const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], time, script, style, option, select") !== null;
        if (text !== "" && !hidden && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
          found.push(text);
        }
      }

      return found;
    };

    expect(untranslated(screen.getByRole("dialog"))).toEqual([]);
    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });

  it("shows a translated banner that passes the accessibility check", async () => {
    sessionStorage.setItem("karry.support-visit", JSON.stringify({ tenantId: nord.id, tenantName: nord.name, sessionId: "0192f0c1-0000-7000-8000-00000000d001", endsAt: new Date(Date.now() + 10 * 60_000).toISOString() }));
    await renderAs("/", fakeApi({ permissions: [], operatorTenants: [nord], supportSessions: [supportSession({ tenantId: nord.id })] }), { pseudo: true });
    const banner = await screen.findByRole("complementary");

    for (const text of [...banner.querySelectorAll("p, button")].map((element) => element.textContent)) {
      expect(text).toContain(pseudoOpen);
    }

    const result = await axe.run(banner, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

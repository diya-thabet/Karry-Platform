import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, user } from "../testing/fakeApi";
import { findTable } from "../testing/tables";

const admin = ["identity.roles.manage", "identity.roles.read", "identity.users.manage", "identity.users.read"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>, options: { language?: "fr" | "en"; pseudo?: boolean; stepUp?: boolean } = {}) {
  const i18n = await createI18n({ language: options.language ?? "fr", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth({ stepUp: options.stepUp ?? true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
  return auth;
}

async function choose(label: string | RegExp, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

describe("settings (US-P1-IAM-04/05/06/07)", () => {
  it("shows only the settings the person may use", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["identity.users.read"] }), { language: "en" });

    expect(await screen.findByRole("link", { name: "People" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Roles" })).not.toBeInTheDocument();
  });

  it("shows everyone the company settings, and nothing else without a permission", async () => {
    await renderAt("/settings", fakeApi({ permissions: [] }), { language: "en" });

    expect(await screen.findByRole("link", { name: "Company" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "People" })).not.toBeInTheDocument();
  });

  it("lists default roles by their translated name and offers a new role only to role managers", async () => {
    await renderAt("/settings/roles", fakeApi({ permissions: ["identity.roles.read"] }));

    const table = await findTable("Rôles");
    expect(await within(table).findByRole("link", { name: "Administrateur" })).toBeInTheDocument();
    expect(within(table).getByRole("link", { name: "Conducteur d’engin" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Nouveau rôle" })).not.toBeInTheDocument();
  });

  it("shows a default role's permissions grouped by module, fixed, with its field levels editable", async () => {
    await renderAt("/settings/roles/site-manager", fakeApi({ permissions: admin }), { language: "en" });

    const users = await screen.findByRole("group", { name: "Users and roles" });
    expect(within(users).getByRole("checkbox", { name: /Invite, change and deactivate people/ })).toBeChecked();
    expect(within(users).getByRole("checkbox", { name: /See roles/ })).not.toBeChecked();
    expect(within(users).getByRole("checkbox", { name: /See people/ })).toBeDisabled();
    expect(screen.getByRole("group", { name: "Audit" })).toBeInTheDocument();
    expect(screen.getByText(/keeps the permissions Karry gives it/)).toBeInTheDocument();
    await vi.waitFor(() => {
      expect(screen.getByRole("combobox", { name: "probe.quote.unit-price" })).toBeEnabled();
    });
  });

  it("creates a role, confirming the person's identity when the API asks for it, without losing the form (AC3)", async () => {
    const session: { auth?: Awaited<ReturnType<typeof signedInAuth>> } = {};
    const api = fakeApi({ permissions: admin, needsStepUp: () => (session.auth?.stepUps ?? 0) === 0 });
    const auth = await renderAt("/settings/roles/new", api, { language: "en" });
    session.auth = auth;

    fireEvent.change(await screen.findByRole("textbox", { name: /Name/ }), { target: { value: "Night dispatch" } });
    fireEvent.click(screen.getByRole("checkbox", { name: /Invite, change and deactivate people/ }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Night dispatch" })).toBeInTheDocument();
    expect(auth.stepUps).toBe(1);
    const posts = api.writes().filter((w) => w.method === "POST");
    expect(posts).toHaveLength(2);
    expect(posts[1]?.body).toMatchObject({ name: "Night dispatch", permissions: ["identity.users.manage"], basedOn: null, office: true });
  });

  it("explains the refusal when the person closes the confirmation window", async () => {
    await renderAt("/settings/roles/new", fakeApi({ permissions: admin, needsStepUp: () => true }), { language: "en", stepUp: false });

    fireEvent.change(await screen.findByRole("textbox", { name: /Name/ }), { target: { value: "Night dispatch" } });
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const dialog = await screen.findByRole("dialog", { name: "Something went wrong" });
    expect(within(dialog).getByText(/Confirm your identity to make this change/)).toBeInTheDocument();
  });

  it("copies an existing role into a new one", async () => {
    const api = fakeApi({ permissions: admin });
    await renderAt("/settings/roles/new", api, { language: "en" });

    fireEvent.change(await screen.findByRole("textbox", { name: /Name/ }), { target: { value: "Deputy" } });
    fireEvent.click(screen.getByRole("combobox", { name: /Start from/ }));
    fireEvent.click(await screen.findByRole("option", { name: "Executive" }));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await screen.findByRole("heading", { level: 1, name: "Deputy" });
    expect(api.writes()[0]?.body).toMatchObject({ name: "Deputy", basedOn: "executive", permissions: ["identity.roles.read", "identity.users.read"] });
  });

  it("changes a person's roles", async () => {
    const api = fakeApi({ permissions: admin, users: [user()] });
    await renderAt("/settings/users", api, { language: "en" });

    fireEvent.click(await screen.findByText("Sami Gharbi"));
    const add = await screen.findByRole("button", { name: "Add a role" });
    await vi.waitFor(() => {
      expect(add).toBeEnabled();
    });
    fireEvent.click(add);
    fireEvent.click(screen.getByRole("button", { name: "Save roles" }));

    await vi.waitFor(() => {
      expect(api.writes()[0]).toMatchObject({ method: "PUT", path: `/users/${user().id}/roles` });
    });
    expect((api.writes()[0]?.body as { roles: unknown[] }).roles).toHaveLength(2);
  });

  it("refuses to overwrite roles another administrator changed meanwhile, and saves over them only when asked (H-C1)", async () => {
    const api = fakeApi({ permissions: admin, users: [user()] });
    await renderAt("/settings/users", api, { language: "en" });
    fireEvent.click(await screen.findByText("Sami Gharbi"));
    const add = await screen.findByRole("button", { name: "Add a role" });
    await vi.waitFor(() => {
      expect(add).toBeEnabled();
    });
    api.changeUserElsewhere(user().id, { roles: [{ role: "executive", siteId: null }] });

    fireEvent.click(add);
    fireEvent.click(screen.getByRole("button", { name: "Save roles" }));

    const notice = await screen.findByRole("alert");
    expect(notice).toHaveTextContent("Someone else saved a change here while you were editing.");
    expect(screen.getAllByRole("combobox", { name: "Role" })).toHaveLength(2);
    fireEvent.click(within(notice).getByRole("button", { name: "Save my changes over theirs" }));
    expect(await screen.findByText("Roles saved")).toBeInTheDocument();
    expect(api.writes().filter((w) => w.method === "PUT").at(-1)?.body).toMatchObject({ roles: [{ role: "commercial" }, { role: "tenant-admin" }] });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("shows the role's other version when the person chooses it over their own field levels (H-C1)", async () => {
    const api = fakeApi({ permissions: admin });
    await renderAt("/settings/roles/site-manager", api, { language: "en" });
    await vi.waitFor(() => {
      expect(screen.getByRole("combobox", { name: "probe.quote.unit-price" })).toBeEnabled();
    });
    api.changeRoleElsewhere("site-manager", { fieldLevels: { "probe.quote.unit-price": "hidden" } });

    await choose("probe.quote.unit-price", "Read");
    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    fireEvent.click(within(await screen.findByRole("alert")).getByRole("button", { name: "Show their version instead" }));

    await vi.waitFor(() => {
      expect(screen.getByRole("combobox", { name: "probe.quote.unit-price" })).toHaveTextContent("Hidden");
    });
    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("tells an account of another company that this is not its address and offers to sign out", async () => {
    const auth = await renderAt("/settings", fakeApi({ permissions: [], failWith: { status: 403, code: "tenant.host_mismatch" } }), { language: "en" });

    const dialog = await screen.findByRole("dialog", { name: "This is not your company's address" }, { timeout: 4000 });
    fireEvent.click(within(dialog).getByRole("button", { name: "Sign out" }));

    await vi.waitFor(() => {
      expect(auth.signOuts).toBe(1);
    });
  });

  it("has no hard-coded text on the role editor (TC-P1-OFF-007)", async () => {
    await renderAt("/settings/roles/site-manager", fakeApi({ permissions: admin }), { pseudo: true });
    await screen.findAllByRole("group");

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], script, style, option, select") !== null;
      // Codes of fields a module declares without translations are shown as they are (by design).
      if (text !== "" && !hidden && !text.includes(pseudoOpen) && !text.includes(pseudoClose) && !text.startsWith("probe.")) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });

  it("has no serious accessibility violation on the role editor", async () => {
    await renderAt("/settings/roles/site-manager", fakeApi({ permissions: admin }));
    await screen.findAllByRole("group");

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

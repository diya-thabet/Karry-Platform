import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, user } from "../testing/fakeApi";

const importer = ["imports.files.manage", "organization.sites.manage", "identity.users.manage", "identity.users.read"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>, options: { language?: "fr" | "en"; pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: options.language ?? "fr", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth();
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

function workbook(name = "sites.xlsx") {
  return new File([new Uint8Array([0x50, 0x4b, 0x03, 0x04])], name, { type: "" });
}

async function upload(name?: string) {
  fireEvent.change(await screen.findByLabelText("Upload the filled-in file"), { target: { files: [workbook(name)] } });
}

const brokenReport = {
  issues: [
    { row: 0, column: null, cell: null, code: "billing.limit_reached", severity: "error", args: { metric: "sites", limit: "1", usage: "1" } },
    { row: 3, column: "code", cell: "A3", code: "imports.key_duplicate", severity: "error", args: { key: "CAR-NORD", rows: "2, 3" } },
    { row: 4, column: "latitude", cell: "I4", code: "imports.number_invalid", severity: "error", args: null },
    { row: 5, column: "name", cell: "B5", code: "imports.formula_value_used", severity: "warning", args: null },
    { row: 6, column: "code", cell: "A6", code: "organization.something_new", severity: "error", args: null },
  ],
  rows: [],
};

describe("Excel import wizard (US-P1-ORG-03)", () => {
  it("is offered in the settings only to those who may import", async () => {
    await renderAt("/settings", fakeApi({ permissions: importer }), { language: "en" });
    expect(await screen.findByRole("link", { name: "Import from Excel" })).toBeInTheDocument();
  });

  it("is not offered without the import permission, and offers nothing without a kind the person may change", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["organization.sites.manage"] }), { language: "en" });
    expect(await screen.findByRole("link", { name: "Company" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Import from Excel" })).not.toBeInTheDocument();
  });

  it("downloads the template of the chosen kind in the chosen language", async () => {
    const api = fakeApi({ permissions: importer });
    await renderAt("/settings/imports", api, { language: "en" });

    expect(await screen.findByRole("combobox", { name: "What to load" })).toHaveTextContent("Sites");
    fireEvent.click(screen.getByRole("button", { name: "Download the template" }));

    await vi.waitFor(() => {
      expect(api.sent.some((s) => s.method === "GET" && s.path === "/api/v1/imports/templates/sites")).toBe(true);
    });
  });

  it("shows every problem on its cell in the person's words and refuses the commit until the file is fixed (AC2)", async () => {
    const api = fakeApi({ permissions: importer, importReport: brokenReport });
    await renderAt("/settings/imports", api, { language: "en" });
    await upload();

    expect(await screen.findByText(/4 rows read, 4 errors: nothing can be saved/, undefined, { timeout: 4000 })).toBeInTheDocument();
    const table = await screen.findByRole("table", { name: "Problems found" });
    expect(within(table).getByText("CAR-NORD is on several rows of the file (2, 3): keep one.")).toBeInTheDocument();
    expect(within(table).getByText("A3")).toBeInTheDocument();
    expect(within(table).getByText("This cell needs a number.")).toBeInTheDocument();
    expect(within(table).getByText(/reached its plan limit/)).toBeInTheDocument();
    expect(within(table).getByText("Warning")).toBeInTheDocument();
    expect(within(table).getByText("Problem organization.something_new. Contact support with this code.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Confirm the import" })).not.toBeInTheDocument();
    expect(api.sent.find((s) => s.method === "POST" && s.path === "/api/v1/imports")).toBeDefined();

    fireEvent.click(screen.getByRole("button", { name: "Discard" }));
    expect(await screen.findByText("Import discarded")).toBeInTheDocument();
    expect(await screen.findByLabelText("Upload the filled-in file")).toBeInTheDocument();
  });

  it("previews what a clean file does, then commits it all at once (AC3)", async () => {
    const api = fakeApi({
      permissions: importer,
      importReport: { issues: [], rows: [{ row: 2, key: "CAR-NORD", action: "create" }, { row: 3, key: "CAR-SUD", action: "update" }, { row: 4, key: "CAR-EST", action: "unchanged" }] },
    });
    await renderAt("/settings/imports", api, { language: "en" });
    await upload();

    const preview = await screen.findByRole("table", { name: "Preview" }, { timeout: 4000 });
    expect(within(preview).getByText("CAR-SUD")).toBeInTheDocument();
    expect(within(preview).getByText("Updated")).toBeInTheDocument();
    expect(screen.getByText("1 created, 1 updated, 1 unchanged.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Confirm the import" }));
    expect(await screen.findByText("Imported: 1 created, 1 updated, 1 unchanged.", undefined, { timeout: 4000 })).toBeInTheDocument();
    expect(api.writes().map((w) => w.path)).toEqual(["/api/v1/imports", expect.stringMatching(/\/commit$/)]);

    const recent = screen.getByRole("table", { name: "Recent imports" });
    expect(within(recent).getByText("sites.xlsx")).toBeInTheDocument();
    expect(await within(recent).findByText("Imported")).toBeInTheDocument();
  });

  it("has no hard-coded text on the report (TC-P1-OFF-007)", async () => {
    await renderAt("/settings/imports", fakeApi({ permissions: importer, importReport: brokenReport }), { pseudo: true });
    const input = await vi.waitFor(() => document.querySelector("input[type=file]") ?? Promise.reject(new Error("no upload yet")));
    fireEvent.change(input, { target: { files: [workbook()] } });
    await screen.findByText("A3", undefined, { timeout: 4000 });

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], script, style, option, select") !== null;
      // Data shown as it is: file name, row numbers, cell references, dates.
      const data = /^(sites\.xlsx|\d+|[A-Z]+\d+|—|.*\d{4}.*)$/.test(text);
      if (text !== "" && !hidden && !data && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });

  it("has no serious accessibility violation on the report", async () => {
    await renderAt("/settings/imports", fakeApi({ permissions: importer, importReport: brokenReport }), { language: "en" });
    await upload();
    await screen.findByRole("table", { name: "Problems found" }, { timeout: 4000 });

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

describe("field user PIN (US-P1-ORG-03, owner decision: no PIN in the file)", () => {
  const operator = user({ id: "0192f0c1-0000-7000-8000-000000000077", kind: "field", firstName: "Hedi", lastName: "Ben Ali", email: null, employeeId: "OP-1", roles: [{ role: "operator", siteId: null }] });

  it("shows an imported field user without a PIN and lets an administrator set one, checked before it is sent", async () => {
    const api = fakeApi({ permissions: importer, users: [operator] });
    await renderAt("/settings/users", api, { language: "en" });

    fireEvent.click(await screen.findByText("Hedi Ben Ali"));
    expect(await screen.findByText("No PIN yet")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Set the PIN" }));

    const dialog = await screen.findByRole("dialog", { name: "Set the PIN" });
    const pin = within(dialog).getByLabelText(/PIN \(6 digits\)/);
    const save = within(dialog).getByRole("button", { name: "Save the PIN" });
    fireEvent.change(pin, { target: { value: "58392" } });
    expect(save).toBeDisabled();
    expect(within(dialog).getByText("Type exactly 6 digits.")).toBeInTheDocument();

    fireEvent.change(pin, { target: { value: "583920" } });
    fireEvent.click(save);

    expect(await screen.findByText("PIN saved")).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "PUT", path: `/users/${operator.id}/pin`, body: { pin: "583920" } }]);
    await vi.waitFor(() => {
      expect(screen.queryByText("No PIN yet")).not.toBeInTheDocument();
    });
    expect(screen.getByRole("button", { name: "Change the PIN" })).toBeInTheDocument();
  });

  it("explains a PIN the API finds too simple", async () => {
    await renderAt("/settings/users", fakeApi({ permissions: importer, users: [operator] }), { language: "en" });

    fireEvent.click(await screen.findByText("Hedi Ben Ali"));
    fireEvent.click(await screen.findByRole("button", { name: "Set the PIN" }));
    const dialog = await screen.findByRole("dialog", { name: "Set the PIN" });
    fireEvent.change(within(dialog).getByLabelText(/PIN \(6 digits\)/), { target: { value: "111111" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Save the PIN" }));

    expect(await screen.findByText(/too easy to guess/)).toBeInTheDocument();
  });

  it("offers no PIN to office users", async () => {
    await renderAt("/settings/users", fakeApi({ permissions: importer, users: [user()] }), { language: "en" });

    fireEvent.click(await screen.findByText("Sami Gharbi"));
    expect(await screen.findByRole("button", { name: "Save roles" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Set the PIN" })).not.toBeInTheDocument();
  });
});

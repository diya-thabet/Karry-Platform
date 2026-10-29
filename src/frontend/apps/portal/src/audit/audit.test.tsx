import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { auditEntry, fakeApi, recordedEvent, site } from "../testing/fakeApi";
import { dayEnd, dayStart } from "./api";

const auditor = ["audit.ledger.read", "audit.ledger.verify", "organization.sites.read"];
const nord = site();
const changed = auditEntry();
const quoted = auditEntry({
  sequence: 2, eventId: "0192f0c1-0000-7000-8000-00000000a002", eventType: "sales.quote_changed.v1", occurredAt: "2026-10-28T10:00:00Z",
  actor: "user-sana", actorName: "Sana Trabelsi", entityType: "quote", entityId: "Q-17",
  event: recordedEvent({ quoteId: "Q-17", before: { customer: "SOTUVER" }, after: { customer: "SOTUVER SA" }, maskedFields: ["unitPrice"] }),
  changes: [{ path: "customer", before: "SOTUVER", after: "SOTUVER SA" }],
});
const seeded = auditEntry({
  sequence: 3, eventId: "0192f0c1-0000-7000-8000-00000000a003", eventType: "organization.site_created.v1", occurredAt: "2026-10-28T11:00:00Z",
  actor: "seed-demo", actorName: null, entityType: "site", entityId: nord.id, siteId: null, event: null, changes: [],
});
const entries = [seeded, quoted, changed];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>, options: { language?: "fr" | "en"; pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: options.language ?? "en", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth();
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function choose(label: string, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

function entryCalls(api: ReturnType<typeof fakeApi>) {
  return api.auditCalls.filter((call) => call.path === "/api/v1/audit/entries");
}

describe("audit viewer (US-P1-AUD-04)", () => {
  it("lists the trail newest first with people's names, records and sites", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, sites: [nord], auditEntries: entries }));

    const table = await screen.findByRole("table", { name: "Audit log" });
    const rows = await within(table).findAllByRole("row");
    expect(rows.slice(1).map((row) => within(row).getAllByRole("cell")[1]?.textContent)).toEqual(["seed-demo", "Sana Trabelsi", "Ali Ben Salah"]);
    const ali = within(table).getByRole("row", { name: /Ali Ben Salah/ });
    expect(within(ali).getByText("assets.asset_changed.v1")).toBeInTheDocument();
    expect(within(ali).getByText(`asset · ${changed.entityId ?? ""}`)).toBeInTheDocument();
    expect(await within(ali).findByText("Carrière Nord")).toBeInTheDocument();
    expect(within(within(table).getByRole("row", { name: /seed-demo/ })).getByText("Whole company")).toBeInTheDocument();
  });

  it("names Karry for an entry no person made", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, auditEntries: [auditEntry({ actor: null, actorName: null })] }));

    expect(await screen.findByText("Karry (automatic)")).toBeInTheDocument();
  });

  it("shows an entry's before and after, the fields the policy hides and the event as recorded (AC1)", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, auditEntries: entries }));

    fireEvent.click(await screen.findByRole("button", { name: "Details of entry no. 2" }));

    const dialog = await screen.findByRole("dialog", { name: "Entry no. 2" });
    expect(within(dialog).getByText(/By Sana Trabelsi/)).toBeInTheDocument();
    const change = within(dialog).getByRole("row", { name: /customer/ });
    expect(within(change).getAllByRole("cell").map((cell) => cell.textContent)).toEqual(["SOTUVER", "SOTUVER SA"]);
    expect(within(dialog).getByText("unitPrice")).toBeInTheDocument();
    expect(within(dialog).getByText("Hidden by your company's policy")).toBeInTheDocument();
    expect(within(dialog).queryByRole("row", { name: /unitPrice/ })).not.toBeInTheDocument();
    expect(within(dialog).getByText("Event as recorded")).toBeInTheDocument();
  });

  it("says so when an entry records an action or an event this version cannot show", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, auditEntries: entries }));

    fireEvent.click(await screen.findByRole("button", { name: "Details of entry no. 3" }));

    const dialog = await screen.findByRole("dialog", { name: "Entry no. 3" });
    expect(within(dialog).getByText(/By seed-demo/)).toBeInTheDocument();
    expect(within(dialog).getByText("This event records an action, not a before/after change.")).toBeInTheDocument();
    expect(within(dialog).getByText("This version of Karry cannot show the content of this event.")).toBeInTheDocument();
  });

  it("filters by person, site, record, event and period, and clears them", async () => {
    const api = fakeApi({ permissions: auditor, sites: [nord], auditEntries: entries });
    await renderAt("/audit", api);
    await screen.findByRole("table", { name: "Audit log" });

    await choose("Person", "Sana Trabelsi");
    await choose("Site", "Carrière Nord");
    fireEvent.change(screen.getByLabelText(/^Event/), { target: { value: " sales.quote_changed.v1 " } });
    fireEvent.change(screen.getByLabelText(/^Kind of record/), { target: { value: "quote" } });
    fireEvent.change(screen.getByLabelText("Record ID"), { target: { value: "Q-17" } });
    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-10-01" } });
    fireEvent.change(screen.getByLabelText("To"), { target: { value: "2026-10-28" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));

    await vi.waitFor(() => {
      expect(entryCalls(api).at(-1)?.query).toEqual({
        actor: "user-sana", siteId: nord.id, eventType: "sales.quote_changed.v1", entityType: "quote", entityId: "Q-17",
        from: dayStart("2026-10-01"), to: dayEnd("2026-10-28"), limit: "50",
      });
    });
    const table = screen.getByRole("table", { name: "Audit log" });
    expect(await within(table).findByText("Sana Trabelsi")).toBeInTheDocument();
    expect(within(table).queryByText("Ali Ben Salah")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Clear" }));
    expect(await within(table).findByText("Ali Ben Salah")).toBeInTheDocument();
    expect(screen.getByRole("combobox", { name: "Person" })).toHaveTextContent("Anyone");
    expect(screen.getByLabelText("Record ID")).toHaveValue("");
  });

  it("refuses a period that ends before it starts without asking the API", async () => {
    const api = fakeApi({ permissions: auditor, auditEntries: entries });
    await renderAt("/audit", api);
    await screen.findByRole("table", { name: "Audit log" });
    const calls = entryCalls(api).length;

    fireEvent.change(screen.getByLabelText("From"), { target: { value: "2026-10-28" } });
    fireEvent.change(screen.getByLabelText(/^To/), { target: { value: "2026-10-27" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));

    expect(await screen.findByText("The end is before the start.")).toBeInTheDocument();
    expect(entryCalls(api)).toHaveLength(calls);
  });

  it("days are whole days where the person is", () => {
    expect(new Date(dayEnd("2026-10-28")).getTime() - new Date(dayStart("2026-10-28")).getTime()).toBe(86_400_000 - 1);
    expect(new Date(dayStart("2026-10-28")).getDate()).toBe(28);
  });

  it("loads older entries page after page from the cursor", async () => {
    const many = Array.from({ length: 51 }, (_, i) => auditEntry({ sequence: 51 - i, eventId: `0192f0c1-0000-7000-8000-${String(51 - i).padStart(12, "0")}` }));
    const api = fakeApi({ permissions: auditor, auditEntries: many });
    await renderAt("/audit", api);
    const table = await screen.findByRole("table", { name: "Audit log" });
    expect(await within(table).findAllByRole("button", { name: /Details of entry/ })).toHaveLength(50);

    fireEvent.click(screen.getByRole("button", { name: "Show more" }));

    expect(await within(table).findByRole("button", { name: "Details of entry no. 1" })).toBeInTheDocument();
    expect(entryCalls(api).at(-1)?.query).toEqual({ limit: "50", cursor: "2" });
    expect(screen.queryByRole("button", { name: "Show more" })).not.toBeInTheDocument();
  });

  it("exports the filtered entries to CSV and says the export is recorded (AC2)", async () => {
    const api = fakeApi({ permissions: auditor, auditEntries: entries });
    await renderAt("/audit", api);
    await screen.findByRole("table", { name: "Audit log" });
    await choose("Person", "Ali Ben Salah");
    fireEvent.click(screen.getByRole("button", { name: "Apply filters" }));
    await vi.waitFor(() => { expect(entryCalls(api).at(-1)?.query.actor).toBe("user-ali"); });

    fireEvent.click(screen.getByRole("button", { name: "Export to Excel (CSV)" }));

    expect(await screen.findByText("Export downloaded and recorded in the audit log")).toBeInTheDocument();
    expect(api.auditCalls.find((call) => call.path === "/api/v1/audit/entries/export.csv")?.query).toEqual({ actor: "user-ali" });
  });

  it("explains an export over the limit", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, auditEntries: entries, exportTooLarge: true }));
    await screen.findByRole("table", { name: "Audit log" });

    fireEvent.click(screen.getByRole("button", { name: "Export to Excel (CSV)" }));

    expect(await screen.findByText("An export holds at most 100,000 entries: narrow the period or the filters.")).toBeInTheDocument();
  });
});

describe("integrity check (US-P1-AUD-03 AC2)", () => {
  it("runs a check and shows the result, the entries checked and who ran it", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, auditEntries: entries }));
    expect(await screen.findByText("The trail has not been checked yet.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Check now" }));

    expect(await screen.findByText("Intact")).toBeInTheDocument();
    expect(screen.getByText("3 entries checked, 1 sealed copy compared.")).toBeInTheDocument();
    expect(screen.getByText(/by Amel Admin\.$/)).toBeInTheDocument();
  });

  it("shows where the trail was tampered with", async () => {
    await renderAt("/audit", fakeApi({
      permissions: auditor, auditEntries: entries, verifyFinds: { valid: false, firstBrokenSequence: 2, problem: "entry hash mismatch" },
    }));

    fireEvent.click(await screen.findByRole("button", { name: "Check now" }));

    expect(await screen.findByText("Tampering found")).toBeInTheDocument();
    expect(screen.getByText(/First broken entry: no\. 2\./)).toBeInTheDocument();
    expect(screen.getByText("entry hash mismatch")).toBeInTheDocument();
  });
});

describe("audit access", () => {
  it("tells people without the audit permissions that the log is not for them", async () => {
    const api = fakeApi({ permissions: [], auditEntries: entries });
    await renderAt("/audit", api);

    expect(await screen.findByText("The audit log is not available to you")).toBeInTheDocument();
    expect(api.auditCalls).toEqual([]);
  });

  it("shows only the log to a reader who may not run the check", async () => {
    await renderAt("/audit", fakeApi({ permissions: ["audit.ledger.read"], auditEntries: entries }));
    expect(await screen.findByRole("table", { name: "Audit log" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Check now" })).not.toBeInTheDocument();
  });

  it("shows only the check to someone who may run it but not read the log", async () => {
    const api = fakeApi({ permissions: ["audit.ledger.verify"], auditEntries: entries });
    await renderAt("/audit", api);
    expect(await screen.findByRole("button", { name: "Check now" })).toBeInTheDocument();
    expect(screen.queryByRole("table", { name: "Audit log" })).not.toBeInTheDocument();
    expect(api.auditCalls.map((call) => call.path)).toEqual(["/api/v1/audit/verifications/latest"]);
  });

  it("is in French", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, auditEntries: entries }), { language: "fr" });
    expect(await screen.findByRole("table", { name: "Journal d’audit" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Vérifier maintenant" })).toBeInTheDocument();
  });

  it("has no hard-coded text (TC-P1-OFF-007)", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, sites: [nord], auditEntries: entries }), { pseudo: true });
    await screen.findAllByRole("button", { name: new RegExp(pseudoOpen.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")) });
    await vi.waitFor(() => { expect(document.querySelectorAll("tbody tr")).toHaveLength(3); });

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], time, script, style, option, select") !== null;
      const data = text === "—";
      if (text !== "" && !hidden && !data && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });

  it("has no serious accessibility violation, with an entry open", async () => {
    await renderAt("/audit", fakeApi({ permissions: auditor, sites: [nord], auditEntries: entries }));
    fireEvent.click(await screen.findByRole("button", { name: "Details of entry no. 2" }));
    await screen.findByRole("dialog", { name: "Entry no. 2" });

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

import { createMemoryHistory } from "@tanstack/react-router";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, supportSession } from "../testing/fakeApi";
import { findTable } from "../testing/tables";

const admin = ["identity.support.manage"];
const running = supportSession();
const emergency = supportSession({
  id: "0192f0c1-0000-7000-8000-00000000d002", mode: "break_glass", operatorName: "Sami Ops", reason: "INC-42: weighbridge totals wrong at night",
  startedAt: "2026-10-27T22:00:00Z", endsAt: "2026-10-27T23:00:00Z", active: false,
});

async function renderAt(path: string, api: ReturnType<typeof fakeApi>, options: { language?: "en" | "fr"; pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: options.language ?? "en", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("support access (US-P1-OPS-03 AC3)", () => {
  afterEach(() => { vi.useRealTimers(); });

  it("lists Karry's support sessions with how they were opened, reachable from settings", async () => {
    await renderAt("/settings", fakeApi({ permissions: admin, supportSessions: [running, emergency] }));

    fireEvent.click(await screen.findByRole("link", { name: "Support access" }));

    const table = await findTable("Support sessions");
    const now = (await within(table).findByText("Anna Support")).closest("tr") as HTMLElement;
    expect(within(now).getByText("With your code")).toBeInTheDocument();
    expect(within(now).getByText("Looking now")).toBeInTheDocument();
    expect(within(now).getByRole("button", { name: /End the session started/ })).toBeInTheDocument();
    const past = within(table).getByText("Sami Ops").closest("tr") as HTMLElement;
    expect(within(past).getByText("Emergency access")).toBeInTheDocument();
    expect(within(past).getByText("INC-42: weighbridge totals wrong at night")).toBeInTheDocument();
    expect(within(past).getByText("Over")).toBeInTheDocument();
    expect(within(past).queryByRole("button")).not.toBeInTheDocument();
    expect(screen.getByText("Look only: support cannot change anything.")).toBeInTheDocument();
  });

  it("hides the screen from people without the permission", async () => {
    const api = fakeApi({ permissions: ["identity.users.read"] });
    await renderAt("/settings", api);

    expect(await screen.findByRole("link", { name: "Company" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Support access" })).not.toBeInTheDocument();
    expect(api.requested).not.toContain("/api/v1/identity/support/sessions");
  });

  it("creates a code to read to Karry support, once confirmed", async () => {
    const api = fakeApi({ permissions: admin });
    await renderAt("/settings/support", api);

    fireEvent.click(await screen.findByRole("button", { name: "Create a support code" }));
    const dialog = await screen.findByRole("dialog", { name: "Create a support code" });
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the code" }));

    expect(await within(dialog).findByLabelText("Support code")).toHaveTextContent("4HQX-2M7D-9RKA-TP3B");
    expect(within(dialog).getByText(/^Valid until /)).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST").map((s) => s.path)).toEqual(["/support/consents"]);
    fireEvent.click(within(dialog).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

  it("replaces an expired code by a notice and offers a new one", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    await renderAt("/settings/support", fakeApi({ permissions: admin }));
    fireEvent.click(await screen.findByRole("button", { name: "Create a support code" }));
    const dialog = await screen.findByRole("dialog", { name: "Create a support code" });
    fireEvent.click(within(dialog).getByRole("button", { name: "Create the code" }));
    await within(dialog).findByLabelText("Support code");

    act(() => { vi.advanceTimersByTime(30 * 60_000); });

    expect(await within(dialog).findByText(/This code has expired/)).toBeInTheDocument();
    expect(within(dialog).queryByLabelText("Support code")).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "New code" }));
    expect(await within(dialog).findByLabelText("Support code")).toBeInTheDocument();
  });

  it("ends a running session after confirmation", async () => {
    const api = fakeApi({ permissions: admin, supportSessions: [running] });
    await renderAt("/settings/support", api);

    fireEvent.click(await screen.findByRole("button", { name: /End the session started/ }));
    const dialog = await screen.findByRole("dialog", { name: "End this support session?" });
    expect(api.sent.filter((s) => s.method === "POST")).toEqual([]);
    fireEvent.click(within(dialog).getByRole("button", { name: "End now" }));

    expect(await screen.findByText("Support session ended")).toBeInTheDocument();
    expect(api.sent.filter((s) => s.method === "POST").map((s) => s.path)).toEqual([`/support/sessions/${running.id}/end`]);
    expect(await screen.findByText("Over")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /End the session started/ })).not.toBeInTheDocument();
  });

  it("explains a session that had already ended", async () => {
    await renderAt("/settings/support", fakeApi({ permissions: admin, supportSessions: [running], refuseWrites: { status: 422, code: "support.session_ended" } }));

    fireEvent.click(await screen.findByRole("button", { name: /End the session started/ }));
    fireEvent.click(within(await screen.findByRole("dialog", { name: "End this support session?" })).getByRole("button", { name: "End now" }));

    expect(await screen.findByText("This support session has already ended.")).toBeInTheDocument();
  });

  it("is in French", async () => {
    await renderAt("/settings/support", fakeApi({ permissions: admin, supportSessions: [emergency] }), { language: "fr" });
    expect(await findTable("Sessions de support")).toBeInTheDocument();
    expect(await screen.findByText("Accès d'urgence")).toBeInTheDocument();
  });

  it("has no hard-coded text and no serious accessibility violation", async () => {
    await renderAt("/settings/support", fakeApi({ permissions: admin, supportSessions: [running, emergency] }), { pseudo: true });
    await vi.waitFor(() => { expect(document.querySelector("table[aria-busy]")).toBeNull(); }); // skeleton rows first
    await vi.waitFor(() => { expect(document.querySelectorAll("tbody tr")).toHaveLength(2); });

    const walker = document.createTreeWalker(document.querySelector("main") ?? document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], time, script, style") !== null;
      if (text !== "" && !hidden && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, integrityFlag, reviewItem, shiftEntry, site } from "../testing/fakeApi";
import { settlementCommentOk } from "./api";

const settler = ["integrity.flags.read", "integrity.flags.settle", "organization.sites.read"];
const gap = integrityFlag({ subject: { type: "meter", id: "0192f0c1-0000-7000-8000-00000000a201" }, version: '"1"' });
const proposal = { reason: "meter_replaced", comment: "New meter fitted on the 9th.", proposedBy: "someone-else", proposedAt: "2026-11-24T08:00:00Z" };

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function choose(within_: HTMLElement, label: string, option: string) {
  fireEvent.click(within(within_).getByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

describe("settling a continuity gap in the portal (US-P2-FO-08, ADR-0037 §7)", () => {
  it("proposes a reason with what happened, warns that unexplained raises a fraud suspicion, and sends the flag's version", async () => {
    const api = fakeApi({ permissions: settler, sites: [site()], integrityFlags: [gap] });
    await renderAt("/assets/integrity", api);

    fireEvent.click(await screen.findByRole("button", { name: "Settle" }));
    const dialog = await screen.findByRole("dialog", { name: "Settle a continuity gap" });
    expect(within(dialog).getByRole("button", { name: "Propose" })).toBeDisabled();
    await choose(dialog, "Reason", "Unexplained");
    expect(within(dialog).getByText("Unexplained raises a fraud suspicion on this meter at once.")).toBeInTheDocument();
    fireEvent.change(within(dialog).getByRole("textbox", { name: "What happened" }), { target: { value: "Nobody knows who ran the engine." } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Propose" }));

    expect(await screen.findByText("Settlement proposed: another person approves it")).toBeInTheDocument();
    expect(api.settlements).toEqual([{ action: "settle", ifMatch: '"1"', body: { reason: "unexplained", comment: "Nobody knows who ran the engine." } }]);
    expect(await screen.findByText("Settlement proposed: Unexplained — Nobody knows who ran the engine.")).toBeInTheDocument();
  });

  it("approves or declines another person's settlement on the flag's version", async () => {
    const api = fakeApi({ permissions: settler, sites: [site()], integrityFlags: [{ ...gap, settlement: proposal }] });
    await renderAt("/assets/integrity", api);

    expect(await screen.findByText("Settlement proposed: Meter replaced — New meter fitted on the 9th.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Approve the settlement" }));

    expect(await screen.findByText("Gap settled")).toBeInTheDocument();
    expect(api.settlements).toEqual([{ action: "settlement/approve", ifMatch: '"1"', body: null }]);
  });

  it("refreshes the list without an error dialog when the gap changed meanwhile", async () => {
    const api = fakeApi({ permissions: settler, sites: [site()], integrityFlags: [{ ...gap, settlement: proposal }] });
    await renderAt("/assets/integrity", api);
    await screen.findByRole("button", { name: "Decline" });
    api.changeFlagElsewhere(gap.id);

    fireEvent.click(screen.getByRole("button", { name: "Decline" }));

    expect(await screen.findByText("This gap changed meanwhile: the list shows it as it is now.")).toBeInTheDocument();
    expect(api.settlements).toEqual([{ action: "settlement/decline", ifMatch: '"1"', body: { comment: null } }]);
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
  });

  it("offers nothing to someone who only reads flags, nor on a flag that is not a continuity gap", async () => {
    await renderAt("/assets/integrity", fakeApi({
      permissions: ["integrity.flags.read", "organization.sites.read"], sites: [site()],
      integrityFlags: [gap, integrityFlag({ id: "0192f0c1-0000-7000-8000-00000000a009", rule: "RULE-FO-03", details: { part: "end", minutesAhead: "25" } })],
    }));

    await screen.findAllByText("Open");
    expect(screen.queryByRole("button", { name: "Settle" })).not.toBeInTheDocument();
  });

  it("has no accessibility violation with the dialog open", async () => {
    await renderAt("/assets/integrity", fakeApi({ permissions: settler, sites: [site()], integrityFlags: [gap] }));
    fireEvent.click(await screen.findByRole("button", { name: "Settle" }));
    await screen.findByRole("dialog", { name: "Settle a continuity gap" });

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("asks for 1 to 500 characters about what happened", () => {
    expect([settlementCommentOk(" "), settlementCommentOk("x"), settlementCommentOk("x".repeat(501))]).toEqual([false, true, false]);
  });
});

describe("the review queue warns a reviewer whose field account is not linked (residual R-15)", () => {
  const reviewer = ["verification.items.read", "verification.items.review", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"];

  it.each([
    [{ linked: false, unlinkedNamesake: true }, true],
    [{ linked: true, unlinkedNamesake: false }, false],
  ] as const)("with %j shows the warning: %s", async (fieldAccount, shown) => {
    await renderAt("/review", fakeApi({ permissions: reviewer, sites: [site()], assets: [asset()], reviewItems: [reviewItem()], entries: [shiftEntry()], fieldAccount }));
    await screen.findAllByText("PEL-01");

    const warning = "A field account has your name but is not linked to you: your reviews cannot tell which shifts you drove yourself. Ask an administrator to link it (Settings › Users).";
    if (shown) {
      expect(await screen.findByText(warning)).toBeInTheDocument();
    } else {
      expect(screen.queryByText(warning)).not.toBeInTheDocument();
    }
  });
});

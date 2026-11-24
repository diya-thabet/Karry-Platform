import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, reviewItem, shiftEntry, site } from "../testing/fakeApi";
import { correctedFields, explanationOk, fuelText, meterText } from "./decisions";

const reviewer = ["verification.items.read", "verification.items.review", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"];
const id = (n: number) => `0192f0c1-0000-7000-8000-${n.toString(16).padStart(12, "0")}`;
const earlier = shiftEntry({ id: id(0xe100), startedAt: "2026-11-22T22:00:00Z", startValue: 1230, endValue: 1242.5, endPhoto: `sha256:${"9".repeat(64)}` });
const entry = shiftEntry();
const item = reviewItem();

async function openReview(api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/review"] })} />);
  fireEvent.click(await screen.findByRole("button", { name: "Review the entry of PEL-01" }));
  return screen.findByRole("dialog", { name: "Review an entry" });
}

function setup(overrides: { permissions?: string[]; items?: ReturnType<typeof reviewItem>[]; correctionWaits?: boolean } = {}) {
  return fakeApi({
    permissions: overrides.permissions ?? reviewer, sites: [site()], assets: [asset()], reviewItems: overrides.items ?? [item], entries: [earlier, entry],
    correctionWaits: overrides.correctionWaits ?? false,
  });
}

describe("deciding on an entry (US-P2-VER-02 … 04, ADR-0038 §4)", () => {
  it("shows the previous end on the meter beside this start and end, then approves the version seen", async () => {
    const api = setup();
    const panel = await openReview(api);

    expect(await within(panel).findByText("Previous end on this meter")).toBeInTheDocument();
    expect(within(panel).getByRole("img", { name: "Previous end on this meter" })).toBeInTheDocument();
    expect(within(panel).getAllByText("1,242.5 h")).toHaveLength(2); // the previous end and this start: continuity holds
    fireEvent.click(within(panel).getByRole("button", { name: "Approve" }));

    expect(await screen.findByText("Entry approved")).toBeInTheDocument();
    expect(api.decisions).toEqual([{ action: "approve", ifMatch: '"1"', body: null }]);
    await waitFor(() => { expect(screen.queryByRole("dialog", { name: "Review an entry" })).not.toBeInTheDocument(); });
  });

  it("rejects only with a reason and 10 to 500 characters about what is wrong", async () => {
    const api = setup();
    const panel = await openReview(api);

    fireEvent.click(within(panel).getByRole("button", { name: "Reject" }));
    const dialog = await screen.findByRole("dialog", { name: "Reject" });
    const send = within(dialog).getByRole("button", { name: "Reject" });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "What is wrong" }), { target: { value: "Too short" } });
    expect(send).toBeDisabled();
    expect(within(dialog).getByText("Write 10 to 500 characters.")).toBeInTheDocument();
    fireEvent.change(within(dialog).getByRole("textbox", { name: "What is wrong" }), { target: { value: "The end photo shows 1 248, not 1 250." } });
    fireEvent.click(send);

    expect(await screen.findByText("Entry rejected: the operator sees why at the next sync")).toBeInTheDocument();
    expect(api.decisions).toEqual([{ action: "reject", ifMatch: '"1"', body: { reasonCode: "wrong_reading", comment: "The end photo shows 1 248, not 1 250." } }]);
  });

  it("sends only the values changed with the reason, and says when the correction waits for another person", async () => {
    const api = setup({ correctionWaits: true });
    const panel = await openReview(api);

    fireEvent.click(within(panel).getByRole("button", { name: "Correct" }));
    const dialog = await screen.findByRole("dialog", { name: "Correct" });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "End (h)" }), { target: { value: "1248,5" } });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Why" }), { target: { value: "The end photo reads 1 248.5." } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Correct" }));

    expect(await screen.findByText("Correction saved: it waits for another person's approval")).toBeInTheDocument();
    expect(api.decisions).toEqual([{ action: "correct", ifMatch: '"1"', body: { fields: { endValue: "1248.5" }, reason: "The end photo reads 1 248.5." } }]);
  });

  it("closes the panel and refreshes the list when someone else decided meanwhile, without an error dialog", async () => {
    const api = setup();
    const panel = await openReview(api);
    api.changeReviewItemElsewhere(item.id);

    fireEvent.click(within(panel).getByRole("button", { name: "Approve" }));

    expect(await screen.findByText("This entry changed meanwhile: the list shows it as it is now.")).toBeInTheDocument();
    expect(screen.queryByRole("alertdialog")).not.toBeInTheDocument();
    await waitFor(() => { expect(screen.queryByRole("dialog", { name: "Review an entry" })).not.toBeInTheDocument(); });
  });

  it("offers no decision to someone who only reads the queue, and the second approval to whom may give it", async () => {
    const readOnly = setup({ permissions: reviewer.filter((p) => p !== "verification.items.review") });
    const panel = await openReview(readOnly);
    await within(panel).findByText("Previous end on this meter");
    expect(within(panel).queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
  });

  it("gives the second approval of a correction waiting for it", async () => {
    const api = setup({ permissions: [...reviewer, "verification.items.second-approve"], items: [reviewItem({ status: "awaiting_second" })] });
    const panel = await openReview(api);

    expect(within(panel).queryByRole("button", { name: "Approve" })).not.toBeInTheDocument();
    fireEvent.click(await within(panel).findByRole("button", { name: "Give the second approval" }));

    expect(await screen.findByText("Entry approved")).toBeInTheDocument();
    expect(api.decisions.map((d) => d.action)).toEqual(["second-approve"]);
  });

  it("has no accessibility violation with the panel open", async () => {
    const panel = await openReview(setup());
    await within(panel).findByText("Previous end on this meter");

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});

describe("what a controller types", () => {
  it.each([
    ["1250", "1250"], ["1 248,5", "1248.5"], ["1248.55", null], ["-3", null], ["", null],
  ] as const)("reads the meter value %j as %j", (text, expected) => {
    expect(meterText(text)).toBe(expected);
  });

  it.each([["0", "0"], ["100", "100"], ["101", null], ["5.5", null]] as const)("reads the fuel level %j as %j", (text, expected) => {
    expect(fuelText(text)).toBe(expected);
  });

  it("keeps only what changed, and nothing when a typed value is not one", () => {
    const typed = { startValue: "1242.5", endValue: "1250", fuelStartPercent: "80", fuelEndPercent: "30" };
    expect(correctedFields(entry, typed)).toEqual({ fuelEndPercent: "30" });
    expect(correctedFields(entry, { ...typed, startValue: "1242,50" })).toBeNull();
    expect(correctedFields(entry, { ...typed, fuelEndPercent: "35" })).toEqual({});
  });

  it("asks for 10 to 500 characters of explanation", () => {
    expect(explanationOk("too short")).toBe(false);
    expect(explanationOk("  ten chars!  ")).toBe(true);
    expect(explanationOk("x".repeat(501))).toBe(false);
  });
});

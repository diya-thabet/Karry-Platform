import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, reviewItem, shiftEntry, site } from "../testing/fakeApi";
import { moved, shortcutOf } from "./shortcuts";

const reviewer = ["verification.items.read", "verification.items.review", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"];
const id = (n: number) => `0192f0c1-0000-7000-8000-${n.toString(16).padStart(12, "0")}`;
const flagged = reviewItem({ id: id(0xe001), recordId: id(0xe101), flags: ["OVERLAP"] });
const first = reviewItem({ id: id(0xe002), recordId: id(0xe102) });
const second = reviewItem({ id: id(0xe003), recordId: id(0xe103) });

async function openQueue(api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/review"] })} />);
  const table = await screen.findByRole("table", { name: "Entries to review" });
  await within(table).findAllByText("PEL-01");
  return table;
}

function setup() {
  return fakeApi({
    permissions: reviewer, sites: [site()], assets: [asset()], reviewItems: [flagged, first, second],
    entries: [shiftEntry({ id: id(0xe101) }), shiftEntry({ id: id(0xe102) }), shiftEntry({ id: id(0xe103) })],
  });
}

const press = (key: string, init: KeyboardEventInit = {}) => { fireEvent.keyDown(window, { key, ...init }); };
const highlighted = (table: HTMLElement) => within(table).getAllByRole("row").findIndex((row) => row.getAttribute("aria-selected") === "true") - 1;

describe("the queue by keyboard and in bulk (US-P2-VER-07, ADR-0038 §4)", () => {
  it("moves with J and K, approves the highlighted entry with A, and lists the keys with ?", async () => {
    const api = setup();
    const table = await openQueue(api);

    expect(highlighted(table)).toBe(0);
    press("j");
    press("j");
    press("j");
    expect(highlighted(table)).toBe(2);
    press("k");
    press("a");

    expect(await screen.findByText("Entry approved")).toBeInTheDocument();
    expect(api.decisions).toEqual([{ action: "approve", ifMatch: '"1"', body: null }]);
    press("?");
    const help = await screen.findByRole("dialog", { name: "Keyboard shortcuts" });
    expect(within(help).getByText("Previous entry")).toBeInTheDocument();
  });

  it("does nothing while the person types in a dialog (TC-P2-VER-011)", async () => {
    const api = setup();
    await openQueue(api);

    press("r");
    const dialog = await screen.findByRole("dialog", { name: "Reject" });
    const comment = within(dialog).getByRole("textbox", { name: "What is wrong" });
    fireEvent.keyDown(comment, { key: "a" });
    fireEvent.keyDown(comment, { key: "j" });
    press("a");

    expect(api.decisions).toEqual([]);
    expect(screen.getByRole("dialog", { name: "Reject" })).toBeInTheDocument();
  });

  it("approves every entry without a flag at once, never a flagged one", async () => {
    const api = setup();
    const table = await openQueue(api);

    expect(within(table).getAllByRole("checkbox")).toHaveLength(2);
    fireEvent.click(screen.getByRole("button", { name: "Choose every entry without a flag" }));
    fireEvent.click(screen.getByRole("button", { name: "Approve 2 entries" }));

    expect(await screen.findByText("2 entries approved")).toBeInTheDocument();
    expect(api.decisions).toEqual([{ action: "approve-many", ifMatch: null, body: { items: [{ id: first.id, version: '"1"' }, { id: second.id, version: '"1"' }] } }]);
  });

  it("approves an entry without a flag with a swipe toward the end, and opens one with a swipe toward the start", async () => {
    const api = setup();
    const table = await openQueue(api);
    const rows = within(table).getAllByRole("row");
    const swipe = (row: HTMLElement | undefined, dx: number) => {
      if (!row) throw new Error("no row");
      fireEvent.pointerDown(row, { pointerType: "touch", clientX: 200, clientY: 10 });
      fireEvent.pointerUp(row, { pointerType: "touch", clientX: 200 + dx, clientY: 12 });
    };

    swipe(rows[1], 150);
    swipe(rows[2], 150);

    await waitFor(() => { expect(api.decisions).toEqual([{ action: "approve", ifMatch: '"1"', body: null }]); });
    swipe(rows[3], -150);
    expect(await screen.findByRole("dialog", { name: "Review an entry" })).toBeInTheDocument();
  });
});

describe("which key presses are shortcuts", () => {
  const empty = { querySelector: () => null };
  const event = (key: string, target: EventTarget | null = document.body, extra: Partial<KeyboardEvent> = {}) =>
    ({ key, ctrlKey: false, metaKey: false, altKey: false, target, ...extra });

  it("reads A, R, C, J, K and ? in either case, and nothing else", () => {
    expect(["a", "R", "c", "J", "k", "?", "x", "Enter"].map((key) => shortcutOf(event(key), empty)))
      .toEqual(["approve", "reject", "correct", "next", "previous", "help", null, null]);
  });

  it("leaves the browser's and the screen reader's keys, text fields and open dialogs alone", () => {
    const input = document.createElement("input");
    const area = document.createElement("textarea");
    expect(shortcutOf(event("a", document.body, { ctrlKey: true }), empty)).toBeNull();
    expect(shortcutOf(event("a", input), empty)).toBeNull();
    expect(shortcutOf(event("a", area), empty)).toBeNull();
    expect(shortcutOf(event("a"), { querySelector: () => document.body })).toBeNull();
  });

  it("stops at the ends of the page", () => {
    expect([moved(0, 3, "previous"), moved(2, 3, "next"), moved(1, 3, "next"), moved(0, 0, "next")]).toEqual([0, 2, 2, -1]);
  });
});

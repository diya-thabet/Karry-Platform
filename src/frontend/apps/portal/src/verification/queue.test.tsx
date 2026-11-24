import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, reviewItem, shiftEntry, site } from "../testing/fakeApi";
import { fetchQueue } from "./queue";

const controller = ["verification.items.read", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"];
const nord = site();
const id = (n: number) => `0192f0c1-0000-7000-8000-${n.toString(16).padStart(12, "0")}`;

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

/** The n-th row of a table's body (rows of the header excluded), failing the test when there is none. */
function row(rows: HTMLElement[], n: number): HTMLElement {
  const found = rows[n];
  if (!found) {
    throw new Error(`no row ${String(n)}`);
  }

  return found;
}

async function choose(label: string, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

/** The overdue item of the three. */
function third<T>(list: T[]): T {
  const found = list[2];
  if (found === undefined) {
    throw new Error("no third item");
  }

  return found;
}

/** Three entries: a plain one, a flagged one, an overdue one (in that creation order). */
function threeEntries() {
  const plain = reviewItem({ id: id(0xe001), recordId: id(0xe101) });
  const flagged = reviewItem({ id: id(0xe002), recordId: id(0xe102), flags: ["RULE-FO-01", "OVERLAP"] });
  const overdue = reviewItem({ id: id(0xe003), recordId: id(0xe103), escalatedAt: "2026-11-24T14:05:00Z" });
  const entries = [
    shiftEntry({ id: id(0xe101) }),
    shiftEntry({ id: id(0xe102), startValue: 1250, endValue: 1262, usage: 12, startPhoto: `sha256:${"3".repeat(64)}`, endPhoto: `sha256:${"4".repeat(64)}` }),
    shiftEntry({ id: id(0xe103), startValue: 1262, endValue: 1270, startPhoto: `sha256:${"5".repeat(64)}`, endPhoto: null, usage: null }),
  ];
  return { items: [plain, flagged, overdue], entries };
}

describe("the review queue (US-P2-VER-01, ADR-0038)", () => {
  it("is reached from the menu and lists overdue, then flagged, then the rest, with the meter, Δ, photos and flags in words", async () => {
    const { items, entries } = threeEntries();
    const api = fakeApi({ permissions: controller, sites: [nord], assets: [asset()], reviewItems: items, entries });
    await renderAt("/", api);

    fireEvent.click(await screen.findByRole("link", { name: "Review" }));

    const table = await screen.findByRole("table", { name: "Entries to review" });
    await within(table).findByText("Overdue");
    const rows = within(table).getAllByRole("row").slice(1);
    expect(rows).toHaveLength(3);
    expect(within(row(rows, 0)).getByText("Overdue")).toBeInTheDocument();
    expect(within(row(rows, 1)).getByText("The start differs from the previous end on this meter")).toBeInTheDocument();
    expect(within(row(rows, 1)).getByText("Another shift of the same operator at the same time")).toBeInTheDocument();
    expect(within(row(rows, 1)).getByText("Δ 12 h")).toBeInTheDocument();
    expect(within(row(rows, 2)).getByText("1,242.5 → 1,250 h")).toBeInTheDocument();
    expect(within(row(rows, 2)).getByText("No flag")).toBeInTheDocument();
    expect(within(row(rows, 2)).getAllByText("PEL-01")).toHaveLength(1);
    expect(within(row(rows, 2)).getByRole("img", { name: "Meter at the start" })).toBeInTheDocument();
    expect(within(row(rows, 0)).queryByRole("img", { name: "Meter at the end" })).not.toBeInTheDocument();
    expect(api.queueCalls[0]?.query).toMatchObject({ sort: "risk", limit: "50" });
    expect(api.requested.filter((path) => path === "/api/v1/fieldops/entries")).toHaveLength(1);
  });

  it("asks the server for one site, one flag or only the overdue entries", async () => {
    const { items, entries } = threeEntries();
    const api = fakeApi({ permissions: controller, sites: [nord], assets: [asset()], reviewItems: items, entries });
    await renderAt("/review", api);

    await choose("Flag", "Another shift of the same operator at the same time");
    await waitFor(() => { expect(screen.getAllByRole("row")).toHaveLength(2); });
    fireEvent.click(screen.getByRole("checkbox", { name: "Overdue only" }));

    expect(await screen.findByText("Nothing to review: every entry of your sites has a decision.")).toBeInTheDocument();
    expect(api.queueCalls.at(-1)?.query).toMatchObject({ flag: "OVERLAP", overdue: "true" });
  });

  it("polls with the page's ETag, keeps the rows on a 304 and shows a new entry when it arrives", async () => {
    const { items, entries } = threeEntries();
    const api = fakeApi({ permissions: controller, reviewItems: items.slice(0, 1), entries });
    const filters = { siteId: "", flag: "", overdue: false };

    const first = await fetchQueue(api.client, filters, true, undefined);
    if (first === "unchanged") throw new Error("the first ask has no tag");
    const again = await fetchQueue(api.client, filters, true, first.etag);
    api.addReviewItem(third(items));
    const changed = await fetchQueue(api.client, filters, true, first.etag);

    expect(first.rows.map((row) => row.item.id)).toEqual([id(0xe001)]);
    expect(again).toBe("unchanged");
    expect(api.queueCalls[1]?.ifNoneMatch).toBe(first.etag);
    expect(changed !== "unchanged" && changed.rows.map((row) => row.item.id)).toEqual([id(0xe003), id(0xe001)]);
  });

  it("shows the items without their values to someone who may not read entries, and asks for no entry", async () => {
    const { items, entries } = threeEntries();
    const api = fakeApi({ permissions: ["verification.items.read"], reviewItems: items, entries });
    await renderAt("/review", api);

    const table = await screen.findByRole("table", { name: "Entries to review" });
    await within(table).findByText("Overdue");
    expect(within(table).queryByText("Δ 12 h")).not.toBeInTheDocument();
    expect(api.requested).not.toContain("/api/v1/fieldops/entries");
  });

  it("has no accessibility violation", async () => {
    const { items, entries } = threeEntries();
    await renderAt("/review", fakeApi({ permissions: controller, sites: [nord], assets: [asset()], reviewItems: items, entries }));
    await screen.findAllByText("PEL-01");

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});

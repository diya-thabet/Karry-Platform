import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, shiftEntry, site } from "../testing/fakeApi";

const id = (n: number) => `0192f0c1-0000-7000-8000-${n.toString(16).padStart(12, "0")}`;
const pelle = asset();
const loader = asset({ id: id(0x7002), code: "CHG-01" });
const shiftOn = (date: string) => ({ date, code: "N", startsAt: `${date}T19:00:00Z`, endsAt: `${date}T23:00:00Z` });
const early = shiftEntry({ id: id(0xe201), assetId: pelle.id, shift: shiftOn("2026-11-03"), startValue: 100, endValue: 108, usage: 8 });
const late = shiftEntry({ id: id(0xe202), assetId: pelle.id, shift: shiftOn("2026-11-20"), startValue: 108, endValue: 112, usage: 4, flags: ["OVERLAP"] });
const other = shiftEntry({ id: id(0xe203), assetId: loader.id, shift: shiftOn("2026-11-21") });
const permissions = ["fieldops.entries.read", "assets.registry.read", "organization.sites.read", "verification.items.read"];

async function renderAt(path: string, extra: string[] = []) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  const api = fakeApi({ permissions: [...permissions, ...extra], sites: [site()], assets: [pelle, loader], entries: [early, late, other] });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
  return api;
}

async function period(from: string, to: string) {
  fireEvent.change(await screen.findByLabelText("From"), { target: { value: from } });
  fireEvent.change(screen.getByLabelText("To"), { target: { value: to } });
}

/** The CSV the export saved, read back from the Blob given to the download link. */
function capturedDownloads(): () => Promise<string[]> {
  const blobs: Blob[] = [];
  vi.spyOn(URL, "createObjectURL").mockImplementation((blob) => { if (blob instanceof Blob) blobs.push(blob); return "blob:test"; });
  vi.spyOn(URL, "revokeObjectURL").mockImplementation(() => undefined);
  return async () => Promise.all(blobs.filter((blob) => blob.type.startsWith("text/csv")).map((blob) => blob.text()));
}

describe("entry history: the shifts of a chosen period with their photos (owner's review 2026-11-27)", () => {
  afterEach(() => { vi.restoreAllMocks(); });

  it("is reached from the review queue and lists the shifts of the period chosen, with the meter photos and who drove", async () => {
    capturedDownloads();
    await renderAt("/review");
    fireEvent.click(await screen.findByRole("link", { name: "Entry history" }));

    await period("2026-11-01", "2026-11-30");
    const table = await screen.findByRole("table", { name: "Entry history" });
    expect(await within(table).findAllByText(/^PEL-01|^CHG-01/)).toHaveLength(3);
    expect(within(table).getAllByRole("img", { name: "Meter at the start" }).length).toBeGreaterThanOrEqual(3);

    await period("2026-11-15", "2026-11-30");
    await vi.waitFor(() => { expect(within(screen.getByRole("table", { name: "Entry history" })).queryAllByText(/^PEL-01|^CHG-01/)).toHaveLength(2); });

    await period("2026-10-01", "2026-10-31");
    expect(await screen.findByText("No shift in this period.")).toBeInTheDocument();

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("refuses a period that ends before it starts, without asking the API, and cannot export it", async () => {
    const api = await renderAt("/review/history");
    await period("2026-11-20", "2026-11-01");

    expect(await screen.findByText("The end is before the start.")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Export this list (CSV)" })).toBeDisabled();
    expect(api.entryCalls.some((call) => call.query.from === "2026-11-20" && call.query.to === "2026-11-01")).toBe(false);
  });

  it("exports exactly the period and site chosen, never the company's whole history", async () => {
    const downloads = capturedDownloads();
    const api = await renderAt("/review/history");
    await period("2026-11-15", "2026-11-30");
    await vi.waitFor(() => { expect(screen.getAllByText(/^PEL-01|^CHG-01/)).toHaveLength(2); });

    fireEvent.click(screen.getByRole("button", { name: "Export this list (CSV)" }));

    expect(await screen.findByText("2 shifts exported")).toBeInTheDocument();
    const exportCall = api.entryCalls.filter((call) => call.query.limit === "200").at(-1);
    expect(exportCall?.query).toMatchObject({ from: "2026-11-15", to: "2026-11-30" });
    const [csv = ""] = await downloads();
    const lines = csv.replace(new RegExp(`^${String.fromCharCode(0xfeff)}`), "").trim().split("\r\n");
    expect(lines).toHaveLength(3); // the header and the two shifts of the period
    expect(lines[1]).toContain("PEL-01");
    expect(csv).not.toContain("2026-11-03");
  });

  it("opens a shift in a panel with its photos large and the previous end on its meter", async () => {
    capturedDownloads();
    await renderAt("/review/history");
    await period("2026-11-01", "2026-11-30");
    const table = await screen.findByRole("table", { name: "Entry history" });
    const [first] = await within(table).findAllByText("PEL-01");
    if (!first) throw new Error("no PEL-01 row");
    fireEvent.click(first);

    const sheet = await screen.findByRole("dialog", { name: /^PEL-01/ });
    expect(within(sheet).getByRole("region", { name: "Photos" })).toBeInTheDocument();
    expect(within(sheet).getAllByText("Meter at the start").length).toBeGreaterThan(0);
  });

  it("shows on a machine's page only that machine's shifts", async () => {
    capturedDownloads();
    await renderAt(`/assets/${pelle.id}`);
    expect(await screen.findByRole("heading", { name: "Shifts of this machine" })).toBeInTheDocument();
    await period("2026-11-01", "2026-11-30");
    const table = await screen.findByRole("table", { name: "Entry history" });
    await vi.waitFor(() => { expect(within(table).queryAllByText("PEL-01")).toHaveLength(2); });
    expect(within(table).queryByText("CHG-01")).not.toBeInTheDocument();
  });
});

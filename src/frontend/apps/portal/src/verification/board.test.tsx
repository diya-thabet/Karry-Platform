import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, reviewItem, shiftEntry, site } from "../testing/fakeApi";
import { boardOf } from "./board";

const id = (n: number) => `0192f0c1-0000-7000-8000-${n.toString(16).padStart(12, "0")}`;
const morning = { date: "2026-11-23", code: "M", name: "Matin", startsAt: "2026-11-23T06:00:00Z", endsAt: "2026-11-23T14:00:00Z", timeZone: "UTC", lengthMinutes: 480 };
const night = { ...morning, code: "N", name: "Nuit", startsAt: "2026-11-23T22:00:00Z", endsAt: "2026-11-24T06:00:00Z" };
const pelle = asset();
const loader = asset({ id: id(0x7002), code: "CHG-01" });
const truck = asset({ id: id(0x7003), code: "DMP-01" });
const parked = asset({ id: id(0x7004), code: "OLD-01", status: "disposed" });
const shift = { date: "2026-11-23", code: "M", startsAt: morning.startsAt, endsAt: morning.endsAt };
const ended = shiftEntry({ id: id(0xe101), assetId: pelle.id, shift, flags: ["OVERLAP"] });
const open = shiftEntry({ id: id(0xe102), assetId: loader.id, shift, status: "open", endValue: null, endPhoto: null, usage: null, endedAt: null });

describe("the shift board (US-P2-VER-08)", () => {
  it("places each machine in service in its shift: not started, running, end missing an hour after the shift, ended", () => {
    const during = boardOf([morning], [pelle, loader, truck, parked], [ended, open], [reviewItem({ recordId: ended.id })], new Date("2026-11-23T12:00:00Z"));
    const after = boardOf([morning], [pelle, loader, truck], [ended, open], [reviewItem({ recordId: ended.id, status: "approved" })], new Date("2026-11-23T15:01:00Z"));

    expect(during[0]?.machines.map((m) => [m.machine.code, m.state, m.review, m.flags])).toEqual([
      ["PEL-01", "ended", "pending", 1], ["CHG-01", "running", "none", 0], ["DMP-01", "not_started", "none", 0],
    ]);
    expect(during[0]?.counts).toEqual({ planned: 3, started: 2, ended: 1, missingEnd: 0, pendingReviews: 1, flagged: 1 });
    expect(after[0]?.machines.map((m) => m.state)).toEqual(["ended", "missing_end", "not_started"]);
    expect(after[0]?.counts).toMatchObject({ missingEnd: 1, pendingReviews: 0 });
  });

  it("counts a machine's segments of one shift together and keeps other shifts and days apart", () => {
    const handover = shiftEntry({ id: id(0xe103), assetId: pelle.id, shift, status: "open", endValue: null, endPhoto: null, usage: null, endedAt: null });
    const nightEntry = shiftEntry({ id: id(0xe104), assetId: truck.id, shift: { ...shift, code: "N" } });
    const otherDay = shiftEntry({ id: id(0xe105), assetId: truck.id, shift: { ...shift, date: "2026-11-22" } });

    const board = boardOf([morning, night], [pelle, truck], [ended, handover, nightEntry, otherDay], [], new Date("2026-11-23T12:00:00Z"));

    expect(board.map((b) => b.machines.map((m) => `${m.machine.code}:${m.state}`))).toEqual([["PEL-01:running", "DMP-01:not_started"], ["PEL-01:not_started", "DMP-01:ended"]]);
  });

  it("is reached from the review queue and shows the site's shifts with their counts and machines", async () => {
    const i18n = await createI18n({ language: "en", pseudo: false });
    const auth = await signedInAuth({ stepUp: true });
    const api = fakeApi({
      permissions: ["verification.items.read", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"],
      sites: [site()], assets: [pelle, loader], instances: [morning], entries: [ended, open], reviewItems: [reviewItem({ recordId: ended.id })],
    });
    render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/review"] })} />);

    fireEvent.click(await screen.findByRole("link", { name: "Shift board" }));
    fireEvent.change(await screen.findByLabelText("Day"), { target: { value: "2026-11-23" } });

    const card = await screen.findByRole("region", { name: /^Matin/ });
    const machines = await within(card).findByRole("list", { name: "Machines" });
    expect(await within(machines).findByText("PEL-01")).toBeInTheDocument();
    expect(within(machines).getAllByText("To review")).toHaveLength(1);
    expect(within(machines).getByText("1 flag")).toBeInTheDocument();
    expect(within(card).getByText("Started")).toBeInTheDocument();

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("US-P2-SIG-05: a reader of shift reports downloads each shift's report from its card", async () => {
    const i18n = await createI18n({ language: "en", pseudo: false });
    const auth = await signedInAuth({ stepUp: true });
    const permissions = ["verification.items.read", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"];
    const api = fakeApi({
      permissions: [...permissions, "documents.shift-reports.read"],
      sites: [site()], assets: [pelle, loader], instances: [morning, night], entries: [ended, open], reviewItems: [],
    });
    render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/review/board"] })} />);
    fireEvent.change(await screen.findByLabelText("Day"), { target: { value: "2026-11-23" } });

    const nightCard = await screen.findByRole("region", { name: /^Nuit/ });
    fireEvent.click(within(nightCard).getByRole("button", { name: "Shift report (PDF)" }));

    await waitFor(() => { expect(api.shiftReportCalls).toEqual([{ siteId: site().id, date: "2026-11-23", shift: "N", language: "en" }]); });
    expect(await screen.findByText("Shift report issued and downloaded")).toBeInTheDocument();
  });

  it("shows no report button to someone who may not read shift reports", async () => {
    const i18n = await createI18n({ language: "en", pseudo: false });
    const auth = await signedInAuth({ stepUp: true });
    const api = fakeApi({
      permissions: ["verification.items.read", "fieldops.entries.read", "assets.registry.read", "organization.sites.read"],
      sites: [site()], assets: [pelle], instances: [morning], entries: [ended], reviewItems: [],
    });
    render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/review/board"] })} />);
    fireEvent.change(await screen.findByLabelText("Day"), { target: { value: "2026-11-23" } });

    await screen.findByRole("region", { name: /^Matin/ });
    expect(screen.queryByRole("button", { name: "Shift report (PDF)" })).not.toBeInTheDocument();
  });
});

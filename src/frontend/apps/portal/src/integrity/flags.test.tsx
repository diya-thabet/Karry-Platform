import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, integrityFlag, site } from "../testing/fakeApi";
import { flagExplanation, type IntegrityFlag } from "./api";

const controller = ["integrity.flags.read", "assets.registry.read", "organization.sites.read"];
const admin = [...controller, "integrity.rules.manage"];
const nord = site();
const sud = site({ id: "0192f0c1-0000-7000-8000-000000005002", code: "CAR-SUD", name: "Carrière Sud" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

async function choose(label: string, option: string) {
  fireEvent.click(await screen.findByRole("combobox", { name: label }));
  fireEvent.click(await screen.findByRole("option", { name: option }));
}

function flagId(n: number) {
  return `0192f0c1-0000-7000-8000-${n.toString(16).padStart(12, "0")}`;
}

const skew = integrityFlag({
  id: flagId(0xb001), rule: "RULE-FO-03", severity: "medium", siteId: sud.id, evidence: [],
  details: { part: "end", reason: "offset", minutesAhead: "-25", deviceTime: "2026-11-19T17:35:00Z", receivedAt: "2026-11-19T18:00:00Z" },
});
const closed = integrityFlag({
  id: flagId(0xb002), closedAt: "2026-11-19T09:00:00Z", closeReason: "re-evaluated",
  details: { meterKind: "km", expectedValue: "80210", foundValue: "80230", difference: "20" },
});

describe("integrity flags in the portal (US-P2-INT-01 AC2, P2.7)", () => {
  it("lists the open flags in words, reachable from the machines page, with severity, evidence and site", async () => {
    await renderAt("/assets", fakeApi({ permissions: controller, sites: [nord, sud], integrityFlags: [integrityFlag(), skew, closed] }));

    fireEvent.click(await screen.findByRole("link", { name: "Integrity flags" }));

    const found = await screen.findByText("The shift started at 1,250 h, but the last shift on this meter ended at 1,242.5 h (gap of 7.5 h).");
    const row = found.closest("tr") as HTMLElement;
    expect(within(row).getByText("Meter continuity")).toBeInTheDocument();
    expect(within(row).getByText("High")).toBeInTheDocument();
    expect(within(row).getByText("Shift entry")).toBeInTheDocument();
    expect(within(row).getByText("2 photos as evidence")).toBeInTheDocument();
    expect(within(row).getByText("Carrière Nord")).toBeInTheDocument();
    expect(within(row).getByText("Open")).toBeInTheDocument();

    const skewRow = screen.getByText("When the end was signed, the tablet's clock was 25 min behind.").closest("tr") as HTMLElement;
    expect(within(skewRow).getByText("Carrière Sud")).toBeInTheDocument();
    expect(within(skewRow).getByText("Medium")).toBeInTheDocument();
    expect(screen.queryByText(/80,230/)).not.toBeInTheDocument();
    // Read-only: nothing to settle here (P2.8), and no way to the rules without the right to change them.
    expect(screen.queryByRole("button", { name: /close|settle/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Integrity rules" })).not.toBeInTheDocument();
  });

  it("filters by status, site and rule, and says why a closed flag was closed", async () => {
    await renderAt("/assets/integrity", fakeApi({ permissions: admin, sites: [nord, sud], integrityFlags: [integrityFlag(), skew, closed] }));
    expect(await screen.findByRole("link", { name: "Integrity rules" })).toHaveAttribute("href", "/settings/integrity");
    await screen.findByText(/The shift started at 1,250 h/);

    await choose("Status", "Closed");
    const row = (await screen.findByText("The shift started at 80,230 km, but the last shift on this meter ended at 80,210 km (gap of 20 km).")).closest("tr") as HTMLElement;
    expect(within(row).getByText("No longer holds after a new check")).toBeInTheDocument();
    expect(within(row).getByText(/^Closed on/)).toBeInTheDocument();
    expect(screen.queryByText(/1,250 h/)).not.toBeInTheDocument();

    await choose("Status", "All");
    await choose("Site", "Carrière Sud");
    expect(await screen.findByText(/25 min behind/)).toBeInTheDocument();
    expect(screen.queryByText(/1,250 h/)).not.toBeInTheDocument();

    await choose("Site", "All sites");
    await choose("Rule", "Site boundary");
    expect(await screen.findByText("No flag")).toBeInTheDocument();
  });

  it("pages through many flags", async () => {
    const many = Array.from({ length: 60 }, (_, i) => integrityFlag({ id: flagId(0xc000 + i), details: { ...integrityFlag().details, foundValue: String(2000 + i) } }));
    await renderAt("/assets/integrity", fakeApi({ permissions: controller, sites: [nord], integrityFlags: many }));

    await screen.findByText(/started at 2,000 h/);
    expect(screen.getAllByText(/^The shift started at/)).toHaveLength(50);
    fireEvent.click(screen.getByRole("button", { name: "Show more" }));

    expect(await screen.findByText(/started at 2,059 h/)).toBeInTheDocument();
    expect(screen.getAllByText(/^The shift started at/)).toHaveLength(60);
    expect(screen.queryByRole("button", { name: "Show more" })).not.toBeInTheDocument();
  });

  it("says when there is nothing open, and has no accessibility violation", async () => {
    await renderAt("/assets/integrity", fakeApi({ permissions: controller, sites: [nord] }));

    expect(await screen.findByText("No open flag")).toBeInTheDocument();
    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("offers no flag list without the right to read flags", async () => {
    await renderAt("/assets", fakeApi({ permissions: ["assets.registry.read"], integrityFlags: [integrityFlag()] }));

    await screen.findByRole("heading", { name: "Machines" });
    expect(screen.queryByRole("link", { name: "Integrity flags" })).not.toBeInTheDocument();
  });
});

describe("what a flag found, in words (US-P2-INT-01 AC2)", () => {
  async function explain(flag: Partial<IntegrityFlag>, language: "en" | "fr" = "en") {
    const i18n = await createI18n({ language, pseudo: false });
    return flagExplanation(i18n.t.bind(i18n), integrityFlag(flag));
  }

  it.each([
    [{ rule: "RULE-FO-02", details: { meterKind: "hours", startValue: "1000", endValue: "1012", delta: "12", minutes: "480", toleranceHours: "0.5" } },
      "From 1,000 to 1,012 h (12 h) in 480 min; the margin allowed is 0.5 h."],
    [{ rule: "RULE-FO-03", details: { part: "start", minutesAhead: "42" } }, "When the start was signed, the tablet's clock was 42 min ahead."],
    [{ rule: "RULE-FO-04", details: { part: "start", metresOutside: "350", accuracyMetres: "12", marginMetres: "200" } },
      "When the start was signed, the tablet was 350 m outside the site (position accurate to 12 m, margin 200 m)."],
    [{ rule: "RULE-FO-05", details: { part: "end" } }, "The meter photo of the end has no record of being taken with the app's camera."],
    [{ rule: "RULE-FO-06", details: { part: "start", otherEntries: "1", sameAsStart: "false" } }, "The meter photo of the start is also on 1 other entry."],
    [{ rule: "RULE-FO-06", details: { part: "end", otherEntries: "0", sameAsStart: "true" } }, "The end photo is the same as the start photo."],
    [{ rule: "RULE-FO-07", details: { type: "shift.start", deviceTime: "2026-11-19T06:00:00Z" } }, "A revoked tablet sent a command (shift.start); it was set aside, not applied."],
    [{ rule: "RULE-FO-09", details: { part: "start", reason: "not_on_site", signer: "x" } }, "The start was signed by someone with no role on this site."],
    [{ rule: "RULE-FO-09", details: { part: "end", reason: "deactivated", deactivatedAt: "2026-11-20T09:30:00Z" } },
      "The end was signed by a person deactivated on Nov 20, 2026."],
    [{ rule: "RULE-FO-02", details: { meterKind: "hours" } },
      "The meter does not go back, and an hour meter does not run longer than the shift plus the allowed margin."],
  ])("%j", async (flag, words) => {
    expect(await explain(flag)).toBe(words);
  });

  it("formats the values the way the reader writes numbers", async () => {
    expect((await explain({}, "fr")).replace(/\s/gu, " ")).toBe("Le poste a commencé à 1 250 h, mais le dernier poste sur ce compteur s’est terminé à 1 242,5 h (écart de 7,5 h).");
  });
});

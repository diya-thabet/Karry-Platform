import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi } from "../testing/fakeApi";
import { thresholdText } from "./api";

const reader = ["integrity.flags.read"];
const admin = ["integrity.flags.read", "integrity.rules.manage"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

const form = async (name: string) => screen.findByRole("form", { name });

describe("integrity rules (US-P2-INT-01 AC3)", () => {
  it("lists every rule with what it checks, reachable from settings; the security rules are locked on", async () => {
    await renderAt("/settings", fakeApi({ permissions: admin }));

    fireEvent.click(await screen.findByRole("link", { name: "Integrity rules" }));

    const geofence = await form("Site boundary");
    expect(within(geofence).getByText(/within the site's drawn boundary/)).toBeInTheDocument();
    expect(within(geofence).getByRole("checkbox", { name: "The rule runs" })).toBeChecked();
    expect(within(geofence).getByRole("textbox", { name: "Margin outside the site (m)" })).toHaveValue("200");
    expect(within(geofence).getByText("From 0 to 5,000 m. Default: 200 m.")).toBeInTheDocument();
    const revoked = await form("Revoked tablet");
    expect(within(revoked).getByText("Security rule: it always runs.")).toBeInTheDocument();
    expect(within(revoked).queryByRole("checkbox")).not.toBeInTheDocument();
    expect(within(revoked).queryByRole("button")).not.toBeInTheDocument();
    expect(within(revoked).getByText("Critical")).toBeInTheDocument();

    const results = await axe.run(geofence, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("saves a new threshold and a switch on the version the form was filled from", async () => {
    const api = fakeApi({ permissions: admin });
    await renderAt("/settings/integrity", api);

    const clock = await form("Tablet clock");
    fireEvent.change(within(clock).getByRole("textbox", { name: "Largest clock difference (min)" }), { target: { value: "30" } });
    fireEvent.click(within(clock).getByRole("checkbox", { name: "The rule runs" }));
    fireEvent.click(within(clock).getByRole("button", { name: "Save" }));

    expect(await screen.findByText("Rule saved")).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "PUT", path: "/api/v1/integrity/rules/RULE-FO-03", body: { enabled: false, thresholds: { maxSkewMinutes: "30" } } }]);
    expect(await within(clock).findByText(/^Changed on/)).toBeInTheDocument();
  });

  it("refuses a threshold out of its range or with too many decimals before asking the server", async () => {
    const api = fakeApi({ permissions: admin });
    await renderAt("/settings/integrity", api);

    const delta = await form("Meter difference");
    const field = within(delta).getByRole("textbox", { name: "Allowed beyond the shift's length (h)" });
    fireEvent.change(field, { target: { value: "0,75" } });

    expect(within(delta).getByText("Enter a number from 0 to 4 h.")).toBeInTheDocument();
    expect(within(delta).getByRole("button", { name: "Save" })).toBeDisabled();
    fireEvent.change(field, { target: { value: "1,5" } });
    fireEvent.click(within(delta).getByRole("button", { name: "Save" }));
    expect(await screen.findByText("Rule saved")).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "PUT", path: "/api/v1/integrity/rules/RULE-FO-02", body: { enabled: true, thresholds: { toleranceHours: "1.5" } } }]);
  });

  it("keeps the entries when another administrator saved the rule meanwhile, and saves them on request", async () => {
    const api = fakeApi({ permissions: admin });
    await renderAt("/settings/integrity", api);
    const geofence = await form("Site boundary");
    fireEvent.change(within(geofence).getByRole("textbox", { name: "Margin outside the site (m)" }), { target: { value: "350" } });
    api.changeRuleElsewhere("RULE-FO-04", { thresholds: [{ name: "marginMetres", value: 500, default: 200, min: 0, max: 5000, decimals: 0, unit: "m" }] });

    fireEvent.click(within(geofence).getByRole("button", { name: "Save" }));
    fireEvent.click(await within(geofence).findByRole("button", { name: "Save my changes over theirs" }));

    expect(await screen.findByText("Rule saved")).toBeInTheDocument();
    expect(api.writes().at(-1)).toEqual({ method: "PUT", path: "/api/v1/integrity/rules/RULE-FO-04", body: { enabled: true, thresholds: { marginMetres: "350" } } });
  });

  it("shows the rules read-only to a person who reads flags, and hides them from the others", async () => {
    await renderAt("/settings/integrity", fakeApi({ permissions: reader }));

    const geofence = await form("Site boundary");
    expect(within(geofence).getByRole("textbox", { name: "Margin outside the site (m)" })).toBeDisabled();
    expect(within(geofence).getByRole("checkbox", { name: "The rule runs" })).toBeDisabled();
    expect(within(geofence).queryByRole("button", { name: "Save" })).not.toBeInTheDocument();
  });

  it("hides the screen from people without the permission", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["organization.sites.read"] }));

    expect(await screen.findByRole("link", { name: "Company" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Integrity rules" })).not.toBeInTheDocument();
  });
});

describe("threshold text", () => {
  const margin = { name: "marginMetres", value: 200, default: 200, min: 0, max: 5000, decimals: 0, unit: "m" };
  const hours = { name: "toleranceHours", value: 0.5, default: 0.5, min: 0, max: 4, decimals: 1, unit: "h" };

  it.each([
    ["250", margin, "250"],
    [" 0 ", margin, "0"],
    ["5001", margin, null],
    ["250.5", margin, null],
    ["-1", margin, null],
    ["1,5", hours, "1.5"],
    ["0.25", hours, null],
    ["4.1", hours, null],
    ["", hours, null],
  ])("%s → %s", (text, threshold, expected) => {
    expect(thresholdText(text, threshold)).toBe(expected);
  });
});

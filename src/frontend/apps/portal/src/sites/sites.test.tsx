import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, site, user } from "../testing/fakeApi";
import { addDays, todayIn } from "./api";
import { findTable } from "../testing/tables";

const manager = ["organization.sites.read", "organization.sites.manage", "organization.shifts.manage"];
const reader = ["organization.sites.read"];
const siteId = site().id;

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("sites (US-P1-ORG-01)", () => {
  it("lists the sites and shows the archived ones only on request", async () => {
    await renderAt("/sites", fakeApi({
      permissions: reader,
      sites: [site(), site({ id: "0192f0c1-0000-7000-8000-000000005002", code: "OLD", name: "Ancienne carrière", status: "archived" })],
    }));

    expect(await screen.findByRole("link", { name: "Carrière Nord" })).toHaveAttribute("href", `/sites/${siteId}`);
    expect(screen.queryByText("Ancienne carrière")).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "New site" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("checkbox", { name: "Show archived sites" }));
    expect(await screen.findByText("Ancienne carrière")).toBeInTheDocument();
    expect(screen.getByText("Archived")).toBeInTheDocument();
  });

  // The longest form of the portal (about 2 s alone): a full parallel run on a small machine can pass 5 s.
  it("creates a site from typed coordinates and opens it (AC1)", { timeout: 15_000 }, async () => {
    const api = fakeApi({ permissions: manager, tenant: { timeZone: "Africa/Tunis" } });
    await renderAt("/sites/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Code/ }), { target: { value: "CAR-SUD" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Name/ }), { target: { value: "Carrière Sud" } });
    fireEvent.change(screen.getByRole("textbox", { name: /Street and number/ }), { target: { value: "Route de Zaghouan" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^City/ }), { target: { value: "Zaghouan" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Latitude/ }), { target: { value: "36,4" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Longitude/ }), { target: { value: "10.14" } });
    expect(screen.getByText("The map cannot be shown on this device. Enter the latitude and longitude above.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Create the site" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Carrière Sud" })).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "POST", path: "/api/v1/organization/sites",
      body: {
        code: "CAR-SUD", name: "Carrière Sud", type: "quarry",
        address: { line: "Route de Zaghouan", city: "Zaghouan", region: null, postalCode: null, country: "TN" },
        location: { latitude: 36.4, longitude: 10.14 }, boundary: null, timeZone: "Africa/Tunis", currency: null,
      },
    }]);
  });

  it("refuses a latitude out of range before asking the API", async () => {
    const api = fakeApi({ permissions: manager });
    await renderAt("/sites/new", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /^Latitude/ }), { target: { value: "91" } });
    fireEvent.change(screen.getByRole("textbox", { name: /^Longitude/ }), { target: { value: "10" } });
    fireEvent.click(screen.getByRole("button", { name: "Create the site" }));

    expect(await screen.findByText("Enter a latitude between -90 and 90.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);
  });

  it("keeps the code, closes the boundary when saving and refuses a boundary under 3 points", async () => {
    const ring: [number, number][] = [[10, 36.8], [10.01, 36.8], [10.01, 36.81], [10, 36.8]];
    const api = fakeApi({ permissions: manager, sites: [site({ boundary: { type: "Polygon", coordinates: [ring] } })] });
    await renderAt(`/sites/${siteId}`, api);

    expect(await screen.findByRole("textbox", { name: /^Code/ })).toBeDisabled();
    expect(screen.getByText("Boundary: 3 points")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Save the site" }));
    expect(await screen.findByText("Site saved.")).toBeInTheDocument();
    expect(api.writes()[0]).toMatchObject({ method: "PUT", path: `/api/v1/organization/sites/${siteId}`, body: { code: "CAR-NORD", boundary: [ring] } });

    fireEvent.click(await screen.findByRole("button", { name: "Remove the last point" }));
    fireEvent.click(screen.getByRole("button", { name: "Save the site" }));
    expect(await screen.findByText("A boundary needs at least 3 points. Add points or clear it.")).toBeInTheDocument();
    expect(api.writes()).toHaveLength(1);
  });

  it("archives a site and shows it read-only until restored", async () => {
    const api = fakeApi({ permissions: manager, sites: [site()] });
    await renderAt(`/sites/${siteId}`, api);

    fireEvent.click(await screen.findByRole("button", { name: "Archive the site" }));

    expect(await screen.findByText("This site is archived: it keeps its history and has no shifts. Restore it to change it.")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /^Name/ })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "New version: 2 × 12 h" })).not.toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "POST", path: `/api/v1/organization/sites/${siteId}/archive`, body: null }]);
  });

  it("shows a site read-only to a person who may not manage sites", async () => {
    await renderAt(`/sites/${siteId}`, fakeApi({ permissions: reader, sites: [site()] }));

    expect(await screen.findByText("Only a company administrator can create or change sites.")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /^Name/ })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Save the site" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Add the day" })).not.toBeInTheDocument();
  });
});

describe("shifts (US-P1-ORG-02)", () => {
  const today = todayIn("Africa/Tunis");

  it("saves the 2 × 12 h preset as the site's first pattern, from today", async () => {
    const api = fakeApi({ permissions: manager, sites: [site()] });
    await renderAt(`/sites/${siteId}`, api);

    fireEvent.click(await screen.findByRole("button", { name: "New version: 2 × 12 h" }));
    const editor = screen.getByRole("form", { name: "New pattern version" });
    expect(within(editor).getByText("The site's first pattern can start on any date.")).toBeInTheDocument();
    expect(within(editor).getByText("Ends the next day.")).toBeInTheDocument();
    fireEvent.click(within(editor).getByRole("button", { name: "Save the version" }));

    expect(await screen.findByText("Shift pattern saved. The coming shifts follow it.")).toBeInTheDocument();
    const everyDay = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    expect(api.writes()).toEqual([{
      method: "PUT", path: `/api/v1/organization/sites/${siteId}/shift-patterns/${today}`,
      body: {
        shifts: [
          { code: "DAY", name: "Day", start: "06:00", end: "18:00", days: everyDay },
          { code: "NIGHT", name: "Night", start: "18:00", end: "06:00", days: everyDay },
        ],
      },
    }]);
    expect(await screen.findByText(/Day 06:00–18:00 · Night 18:00–06:00/)).toBeInTheDocument();
  });

  it("starts a later version after today and lets only a future version be removed", async () => {
    const tomorrow = addDays(today, 1);
    const shifts = [{ code: "D", name: "Day", start: "07:00", end: "15:00", days: ["mon", "tue", "wed", "thu", "fri"] }];
    const api = fakeApi({
      permissions: manager, sites: [site()],
      patterns: { [siteId]: [
        { id: "p1", effectiveFrom: "2026-01-01", changedAt: "2026-01-01T00:00:00Z", shifts },
        { id: "p2", effectiveFrom: addDays(today, 5), changedAt: "2026-01-01T00:00:00Z", shifts },
      ] },
    });
    await renderAt(`/sites/${siteId}`, api);

    expect(await screen.findByRole("button", { name: `Remove the version of ${addDays(today, 5)}` })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Remove the version of 2026-01-01" })).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "New version: 3 × 8 h" }));
    const editor = screen.getByRole("form", { name: "New pattern version" });
    expect(within(editor).getByLabelText(/^Applies from/)).toHaveValue(tomorrow);
    fireEvent.click(within(within(editor).getByRole("group", { name: "Shift 2" })).getByRole("button", { name: "Remove this shift" }));
    fireEvent.click(within(editor).getByRole("button", { name: "Save the version" }));

    await vi.waitFor(() => {
      expect(api.writes()[0]).toMatchObject({ method: "PUT", path: `/api/v1/organization/sites/${siteId}/shift-patterns/${tomorrow}` });
    });
    expect((api.writes()[0]?.body as { shifts: { code: string }[] }).shifts.map((s) => s.code)).toEqual(["M", "N"]);
  });

  it("adds and removes a non-working day", async () => {
    const date = addDays(today, 10);
    const api = fakeApi({ permissions: manager, sites: [site()] });
    await renderAt(`/sites/${siteId}`, api);

    const form = await screen.findByRole("form", { name: "Add the day" });
    fireEvent.change(within(form).getByLabelText(/^Date/), { target: { value: date } });
    fireEvent.change(within(form).getByRole("textbox", { name: /^Name/ }), { target: { value: "Fête de l’évacuation" } });
    fireEvent.click(within(form).getByRole("button", { name: "Add the day" }));

    expect(await screen.findByText("Non-working day saved.")).toBeInTheDocument();
    expect(api.writes()[0]).toEqual({
      method: "PUT", path: `/api/v1/organization/sites/${siteId}/non-working-days/${date}`, body: { kind: "public-holiday", name: "Fête de l’évacuation" },
    });

    fireEvent.click(await screen.findByRole("button", { name: `Remove ${date}` }));
    expect(await screen.findByText("Non-working day removed.")).toBeInTheDocument();
    expect(api.writes()[1]).toMatchObject({ method: "DELETE", path: `/api/v1/organization/sites/${siteId}/non-working-days/${date}` });
  });

  it("shows the coming shifts on the site's clock with their real length", async () => {
    await renderAt(`/sites/${siteId}`, fakeApi({
      permissions: reader, sites: [site()],
      instances: [{
        date: "2026-10-24", code: "NIGHT", name: "Night", startsAt: "2026-10-24T17:00:00Z", endsAt: "2026-10-25T06:00:00Z", timeZone: "Africa/Tunis", lengthMinutes: 780,
      }],
    }));

    const table = await findTable("Coming shifts");
    expect(await within(table).findByText("Night")).toBeInTheDocument();
    expect(within(table).getByText("18:00–07:00")).toBeInTheDocument();
    expect(within(table).getByText("13 h")).toBeInTheDocument();
  });

  it("has no accessibility violations", async () => {
    await renderAt(`/sites/${siteId}`, fakeApi({ permissions: manager, sites: [site()] }));
    fireEvent.click(await screen.findByRole("button", { name: "New version: 3 × 8 h" }));
    const results = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});

describe("roles on a site (US-P1-IAM-05 with US-P1-ORG-01)", () => {
  it("gives a person a role on one site", async () => {
    const api = fakeApi({ permissions: ["identity.users.manage", "identity.users.read", "identity.roles.read", ...reader], users: [user()], sites: [site()] });
    await renderAt("/settings/users", api);

    fireEvent.click(await screen.findByText("Sami Gharbi"));
    const where = await screen.findByRole("combobox", { name: "Where" });
    await vi.waitFor(() => {
      expect(where).toBeEnabled();
    });
    fireEvent.click(where);
    fireEvent.click(await screen.findByRole("option", { name: "CAR-NORD · Carrière Nord" }));
    fireEvent.click(screen.getByRole("button", { name: "Save roles" }));

    await vi.waitFor(() => {
      expect(api.writes()[0]).toMatchObject({ method: "PUT", path: `/users/${user().id}/roles` });
    });
    expect((api.writes()[0]?.body as { roles: { siteId: string | null }[] }).roles[0]?.siteId).toBe(siteId);
  });
});

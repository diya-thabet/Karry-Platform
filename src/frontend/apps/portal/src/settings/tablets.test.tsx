import { createMemoryHistory } from "@tanstack/react-router";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { device, fakeApi, site } from "../testing/fakeApi";

const manager = ["identity.devices.manage", "organization.sites.read"];
const north = site();
const closed = site({ id: "0192f0c1-0000-7000-8000-000000005002", code: "CAR-SUD", name: "Carrière Sud", status: "archived" });

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("site tablets (US-P1-OFF-02)", () => {
  afterEach(() => { vi.useRealTimers(); });

  it("lists the tablets with their site and status, reachable from settings", async () => {
    await renderAt("/settings", fakeApi({ permissions: manager, sites: [north], devices: [device(), device({ id: "0192f0c1-0000-7000-8000-00000000d002", name: "Ancienne", status: "revoked" })] }));

    fireEvent.click(await screen.findByRole("link", { name: "Site tablets" }));

    const row = (await screen.findByText("Tablette pesage")).closest("tr") as HTMLElement;
    expect(within(row).getByText("Galaxy Tab A9 · Android 14")).toBeInTheDocument();
    expect(await within(row).findByText("CAR-NORD · Carrière Nord")).toBeInTheDocument();
    expect(within(row).getByText("Active")).toBeInTheDocument();
    const old = screen.getByText("Ancienne").closest("tr") as HTMLElement;
    expect(within(old).getByText("Revoked")).toBeInTheDocument();
    expect(within(old).queryByRole("button")).not.toBeInTheDocument();
  });

  it("hides the screen from people without the permission", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["organization.sites.read"] }));

    expect(await screen.findByRole("link", { name: "Company" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Site tablets" })).not.toBeInTheDocument();
  });

  it("creates a code for the only active site and shows it as a QR code and as text (AC1)", async () => {
    const api = fakeApi({ permissions: manager, sites: [north, closed] });
    await renderAt("/settings/tablets", api);

    fireEvent.click(await screen.findByRole("button", { name: "Add a tablet" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(await within(dialog).findByRole("button", { name: "Create the code" }));

    expect(await within(dialog).findByRole("img", { name: "QR code to add the tablet" })).toBeInTheDocument();
    expect(within(dialog).getByText("7KQ2-M9XD-4RTA-0PZB")).toBeInTheDocument();
    expect(within(dialog).getByText(/^Valid until/)).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "POST", path: "/devices/enrollments", body: { siteId: north.id } }]);

    const results = await axe.run(dialog, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations).toEqual([]);
  });

  it("replaces an expired code's QR code by a notice and offers a new code", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const api = fakeApi({ permissions: manager, sites: [north] });
    await renderAt("/settings/tablets", api);
    fireEvent.click(await screen.findByRole("button", { name: "Add a tablet" }));
    const dialog = await screen.findByRole("dialog");
    fireEvent.click(await within(dialog).findByRole("button", { name: "Create the code" }));
    await within(dialog).findByRole("img", { name: "QR code to add the tablet" });

    act(() => { vi.advanceTimersByTime(15 * 60_000); });

    expect(await within(dialog).findByText("This code has expired. Create a new one.")).toBeInTheDocument();
    expect(within(dialog).queryByRole("img")).not.toBeInTheDocument();
    expect(within(dialog).queryByText("7KQ2-M9XD-4RTA-0PZB")).not.toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "New code" }));
    expect(await within(dialog).findByRole("img", { name: "QR code to add the tablet" })).toBeInTheDocument();
    expect(api.writes()).toHaveLength(2);
  });

  it("revokes a tablet after confirmation (AC2)", async () => {
    const api = fakeApi({ permissions: manager, sites: [north], devices: [device()] });
    await renderAt("/settings/tablets", api);

    fireEvent.click(await screen.findByRole("button", { name: "Revoke Tablette pesage" }));
    const dialog = await screen.findByRole("dialog", { name: "Revoke Tablette pesage?" });
    expect(within(dialog).getByText(/kept for review and never processed/)).toBeInTheDocument();
    fireEvent.click(within(dialog).getByRole("button", { name: "Revoke the tablet" }));

    expect(await screen.findByText("Tablet revoked")).toBeInTheDocument();
    const row = (await screen.findByText("Tablette pesage")).closest("tr") as HTMLElement;
    expect(await within(row).findByText("Revoked")).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "POST", path: `/devices/${device().id}/revoke`, body: null }]);
  });
});

import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, hold } from "../testing/fakeApi";

const manager = ["fieldops.entries.read", "fieldops.holds.release", "assets.registry.read"];
const reader = ["fieldops.entries.read", "assets.registry.read"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("machines held as unsafe (US-P2-FO-02 AC3, P2.6)", () => {
  it("lists the held machines, reachable from the machines page, without a release button for a reader", async () => {
    await renderAt("/assets", fakeApi({ permissions: reader, holds: [hold()] }));

    fireEvent.click(await screen.findByRole("link", { name: "Machines held as unsafe" }));

    const row = (await screen.findByRole("link", { name: "PEL-01" })).closest("tr") as HTMLElement;
    expect(within(row).getByText("brakes")).toBeInTheDocument();
    expect(within(row).getByText("Held")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Release" })).not.toBeInTheDocument();
  });

  it("TC-P2-FO-014: a manager releases a hold with a reason of at least ten characters", async () => {
    const api = fakeApi({ permissions: manager, holds: [hold()] });
    await renderAt("/assets/holds", api);

    fireEvent.click(await screen.findByRole("button", { name: "Release" }));
    const dialog = await screen.findByRole("dialog", { name: "Release PEL-01" });
    fireEvent.change(within(dialog).getByRole("textbox", { name: "Reason" }), { target: { value: "fixed" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Release the machine" }));
    expect(await within(dialog).findByText("Write 10 to 500 characters.")).toBeInTheDocument();
    expect(api.writes()).toEqual([]);

    fireEvent.change(within(dialog).getByRole("textbox", { name: "Reason" }), { target: { value: "  Brake pads replaced and tested  " } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Release the machine" }));

    expect(await screen.findByText("PEL-01 released")).toBeInTheDocument();
    expect(api.writes()).toEqual([{ method: "POST", path: "/api/v1/fieldops/holds/0192f0c1-0000-7000-8000-000000009001/release", body: { reason: "Brake pads replaced and tested" } }]);
    expect(await screen.findByText("No machine is held")).toBeInTheDocument();
  });
});

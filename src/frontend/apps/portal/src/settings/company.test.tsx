import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi } from "../testing/fakeApi";

const manager = ["tenancy.settings.manage"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>, language: "fr" | "en" = "en") {
  const i18n = await createI18n({ language, pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
  return i18n;
}

afterEach(() => {
  localStorage.clear();
});

describe("company settings (US-P1-TEN-04)", () => {
  it("saves the settings and shows the change at once, without signing in again (AC2)", async () => {
    const api = fakeApi({ permissions: manager, tenant: { language: "en" } });
    await renderAt("/settings/company", api);

    const legalName = await screen.findByRole("textbox", { name: /Legal name/ });
    fireEvent.change(legalName, { target: { value: "Carrières du Nord SA" } });
    fireEvent.change(screen.getByRole("textbox", { name: /Brand colour/ }), { target: { value: "#1F4E79" } });
    fireEvent.click(screen.getByRole("button", { name: "Save the settings" }));

    expect(await screen.findByText("Settings saved. Everyone sees them now.")).toBeInTheDocument();
    expect(api.writes()).toEqual([
      {
        method: "PUT", path: "/api/v1/tenancy/settings",
        body: { legalName: "Carrières du Nord SA", primaryColour: "#1F4E79", language: "en", currency: "EUR", unitSystem: "metric", timeZone: "UTC", fiscalYearStartMonth: 1 },
      },
    ]);
    expect(await screen.findByRole("textbox", { name: /Legal name/ })).toHaveValue("Carrières du Nord SA");
  });

  it("shows the settings read-only to a person without the permission", async () => {
    await renderAt("/settings/company", fakeApi({ permissions: [], tenant: { currency: "TND" } }));

    expect(await screen.findByText("Only a company administrator can change these settings.")).toBeInTheDocument();
    expect(screen.getByRole("textbox", { name: /Legal name/ })).toBeDisabled();
    expect(screen.getByRole("combobox", { name: "Functional currency" })).toHaveTextContent("TND");
    expect(screen.queryByRole("button", { name: "Save the settings" })).not.toBeInTheDocument();
    expect(screen.queryByText("Choose a logo")).not.toBeInTheDocument();
  });

  it("uploads a logo as the image itself and shows it", async () => {
    const api = fakeApi({ permissions: manager });
    await renderAt("/settings/company", api);

    const input = (await screen.findByText("Choose a logo")).querySelector("input");
    if (!input) {
      throw new Error("no file input");
    }

    fireEvent.change(input, { target: { files: [new File([new Uint8Array([0x89, 0x50, 0x4e, 0x47])], "logo.png", { type: "image/png" })] } });

    expect(await screen.findByRole("img", { name: "Logo of Carrière A" })).toHaveAttribute("src", expect.stringContaining("/carriere-a/logo?v="));
    expect(api.writes()).toEqual([{ method: "PUT", path: "/api/v1/tenancy/settings/logo", body: null }]);
  });

  it("explains a refused colour", async () => {
    await renderAt("/settings/company", fakeApi({ permissions: manager, refuseWrites: { status: 400, code: "tenant.colour_contrast" } }));

    fireEvent.click(await screen.findByRole("button", { name: "Save the settings" }));

    const dialog = await screen.findByRole("dialog", { name: "Something went wrong" });
    expect(within(dialog).getByText("This colour is too light for white text. Choose a darker shade.")).toBeInTheDocument();
  });

  it("shows the company's name, logo and colour in the header", async () => {
    await renderAt("/", fakeApi({ permissions: [], tenant: { name: "Carrière du Nord", logoUrl: "/api/v1/tenancy/public/nord/logo?v=1", primaryColour: "#1F4E79" } }));

    const banner = await screen.findByRole("banner");
    expect(await within(banner).findByText("Carrière du Nord")).toBeInTheDocument();
    expect(banner.querySelector("img")).toHaveAttribute("src", "/api/v1/tenancy/public/nord/logo?v=1");
  });

  it("uses the company's language for a person who never chose one, and keeps a person's own choice (US-P1-OFF-06 AC1)", async () => {
    const i18n = await renderAt("/", fakeApi({ permissions: [], tenant: { language: "en" } }), "fr");
    await vi.waitFor(() => { expect(i18n.language).toBe("en"); });
    expect(document.documentElement.lang).toBe("en");
  });

  it("keeps the person's own language over the company's", async () => {
    localStorage.setItem("karry.language", "fr");
    const i18n = await renderAt("/", fakeApi({ permissions: [], tenant: { language: "en" } }), "fr");
    await screen.findByRole("banner");
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(i18n.language).toBe("fr");
  });

  it("has no accessibility violations", async () => {
    await renderAt("/settings/company", fakeApi({ permissions: manager }));
    await screen.findByRole("button", { name: "Save the settings" });
    const results = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});

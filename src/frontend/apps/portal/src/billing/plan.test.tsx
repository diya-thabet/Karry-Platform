import { createMemoryHistory } from "@tanstack/react-router";
import { render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi } from "../testing/fakeApi";
import { findTable } from "../testing/tables";

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

const reader = ["billing.subscription.read"];

describe("Plan & usage (US-P1-BIL-04 AC2, AC3)", () => {
  it("shows the plan, its state and the usage against each limit", async () => {
    await renderAt("/settings/plan", fakeApi({ permissions: reader }));

    expect(await screen.findByRole("heading", { name: "Professional" })).toBeInTheDocument();
    expect(screen.getByText("Trial")).toBeInTheDocument();
    const table = await findTable("Usage");
    const row = await within(table).findByRole("row", { name: /Office users/ });
    expect(within(row).getByText("3")).toBeInTheDocument();
    expect(within(row).getByText("15")).toBeInTheDocument();
    expect(within(row).getByText("Within the plan")).toBeInTheDocument();
    expect(screen.getByText("Maintenance")).toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations).toEqual([]);
  });

  it("warns billing readers above a limit and tells them when nothing more can be added", async () => {
    await renderAt("/", fakeApi({
      permissions: reader,
      usage: [
        { metric: "office-users", usage: 16, limit: 15, ceiling: 17, state: "over" },
        { metric: "assets", usage: 22, limit: 20, ceiling: 22, state: "blocked" },
      ],
    }));

    const banner = await screen.findByRole("status");
    expect(banner).toHaveTextContent("has reached its plan limit for: Office users, Assets");
    expect(within(banner).getByRole("link", { name: "Plan & usage" })).toHaveAttribute("href", "/settings/plan");
  });

  it("shows no banner and no settings link to people who do not read billing", async () => {
    const api = fakeApi({ permissions: ["identity.users.read"], usage: [{ metric: "assets", usage: 30, limit: 20, ceiling: 22, state: "blocked" }] });
    await renderAt("/settings", api);

    expect(await screen.findByRole("link", { name: "People" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Plan & usage" })).not.toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(api.sent.some((s) => s.path.includes("billing/usage"))).toBe(false);
  });
});

describe("the over-limit banner's kill switch (US-P1-PLT-05, ADR-0019)", () => {
  it("hides the banner when the flag is off for the company", async () => {
    await renderAt("/settings/plan", fakeApi({
      permissions: reader,
      flags: { "portal.usage-banner": false },
      usage: [{ metric: "assets", usage: 22, limit: 20, ceiling: 22, state: "blocked" }],
    }));

    expect(await findTable("Usage")).toBeInTheDocument();
    await new Promise((resolve) => setTimeout(resolve, 50)); // the flag provider has loaded
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });
});

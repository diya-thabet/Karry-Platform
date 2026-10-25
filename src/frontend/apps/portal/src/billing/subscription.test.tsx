import { createMemoryHistory } from "@tanstack/react-router";
import { render, screen } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi } from "../testing/fakeApi";

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("subscription states in the portal (US-P1-BIL-05)", () => {
  it("tells everyone office changes are paused while restricted, and field capture continues", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["identity.users.read"], status: "restricted" }));

    expect(await screen.findByRole("status")).toHaveTextContent("Office changes are paused until the subscription is paid");
    expect(await screen.findByRole("link", { name: "People" })).toBeInTheDocument();
  });

  it("reminds only billing readers of an overdue payment", async () => {
    await renderAt("/", fakeApi({ permissions: ["billing.subscription.read"], status: "past_due" }));
    expect(await screen.findByRole("status")).toHaveTextContent("payment is overdue");
  });

  it("does not show the payment reminder to other people", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["identity.users.read"], status: "past_due" }));
    expect(await screen.findByRole("link", { name: "People" })).toBeInTheDocument();
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
  });

  it("shows only the ended screen once the subscription has ended, with Plan & usage for billing readers (AC3)", async () => {
    await renderAt("/settings", fakeApi({ permissions: ["billing.subscription.read", "identity.users.read"], status: "cancelled" }));

    expect(await screen.findByRole("heading", { name: "Your company's subscription has ended" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "People" })).not.toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Plan & usage" })).toHaveAttribute("href", "/settings/plan");
  });

  it("still opens Plan & usage for billing readers after the end", async () => {
    await renderAt("/settings/plan", fakeApi({ permissions: ["billing.subscription.read"], status: "expired" }));

    expect(await screen.findByRole("table", { name: "Usage" })).toBeInTheDocument();
  });
});

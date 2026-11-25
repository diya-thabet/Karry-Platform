import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi } from "../testing/fakeApi";

const admin = ["notifications.rules.manage"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
  return auth;
}

describe("the company's notice rules (ADR-0039 §2, P2.10 slice 4)", () => {
  it("is reached from settings by an administrator and lists each kind of notice, who hears of it and how", async () => {
    await renderAt("/settings", fakeApi({ permissions: admin }));

    fireEvent.click(await screen.findByRole("link", { name: "Notification rules" }));
    const unsafe = await screen.findByRole("region", { name: "Machine held as unsafe" });
    expect(within(unsafe).getByText(/site's managers and maintenance planners/)).toBeInTheDocument();
    expect(within(unsafe).getByText("By: In the app and Push alert")).toBeInTheDocument();
    expect(within(unsafe).getByRole("checkbox", { name: "Machine held as unsafe: Sent by the company" })).toBeChecked();
  });

  it("is not offered in settings without the permission", async () => {
    await renderAt("/settings", fakeApi({ permissions: [] }));

    expect(await screen.findByRole("link", { name: "Company" })).toBeInTheDocument();
    expect(screen.queryByRole("link", { name: "Notification rules" })).not.toBeInTheDocument();
  });

  it("switches a rule off after confirming the person's identity, says who changed it, and on again", async () => {
    const session: { auth?: Awaited<ReturnType<typeof signedInAuth>> } = {};
    const api = fakeApi({ permissions: admin, needsStepUp: () => (session.auth?.stepUps ?? 0) === 0 });
    session.auth = await renderAt("/settings/notifications", api);

    const box = await screen.findByRole("checkbox", { name: "Fraud suspected: Sent by the company" });
    fireEvent.click(box);

    expect(await screen.findByText("This kind of notice is no longer sent.")).toBeInTheDocument();
    expect(session.auth.stepUps).toBe(1);
    await waitFor(() => { expect(box).not.toBeChecked(); });
    expect(within(screen.getByRole("region", { name: "Fraud suspected" })).getByText(/by admin-1$/)).toBeInTheDocument();

    fireEvent.click(box);
    expect(await screen.findByText("This kind of notice is sent again.")).toBeInTheDocument();
    expect(api.writes().filter((w) => w.method === "PUT").map((w) => [w.path, w.body])).toEqual([
      ["/api/v1/notifications/rules/integrity.fraud_suspected", { enabled: false }],
      ["/api/v1/notifications/rules/integrity.fraud_suspected", { enabled: false }],
      ["/api/v1/notifications/rules/integrity.fraud_suspected", { enabled: true }],
    ]);
  });

  it("shows the rules read-only to a person who may not switch them", async () => {
    await renderAt("/settings/notifications", fakeApi({ permissions: [] }));

    expect(await screen.findByRole("checkbox", { name: "Shift end missing: Sent by the company" })).toBeDisabled();
  });

  it("has no accessibility violation", async () => {
    await renderAt("/settings/notifications", fakeApi({ permissions: admin }));
    await screen.findByRole("region", { name: "Rejected entry" });

    const results = await axe.run(document.body, { rules: { region: { enabled: false } } });
    expect(results.violations).toEqual([]);
  });
});

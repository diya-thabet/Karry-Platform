import { createMemoryHistory } from "@tanstack/react-router";
import { fireEvent, render, screen } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { fakeApi } from "../testing/fakeApi";
import { policyInput } from "./api";

const administrator = ["verification.items.read", "verification.policies.manage"];
const reader = ["verification.items.read"];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>) {
  const i18n = await createI18n({ language: "en", pseudo: false });
  const auth = await signedInAuth({ stepUp: true });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: [path] })} />);
}

describe("review policies (US-P2-VER-05, ADR-0037 §5)", () => {
  it("shows the defaults, then publishes a new version reached from Settings and lists it", async () => {
    const api = fakeApi({ permissions: administrator });
    await renderAt("/settings", api);

    fireEvent.click(await screen.findByRole("link", { name: /Review policies/ }));
    expect(await screen.findByText("The defaults apply: no version has been published.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("checkbox", { name: "Approve entries without flags automatically" }));
    fireEvent.change(screen.getByRole("textbox", { name: /Review within/ }), { target: { value: "12" } });
    fireEvent.click(screen.getByRole("button", { name: "Publish a new version" }));

    expect(await screen.findByText("Version 1 published")).toBeInTheDocument();
    expect(api.writes()).toEqual([{
      method: "PUT", path: "/api/v1/verification/policies/fieldops.shift_entry",
      body: { autoApprove: true, fourEyesHours: 2, fourEyesShare: 0.05, slaHours: 12, escalateTo: "site-manager", effectiveFrom: null },
    }]);
    expect(await screen.findByRole("table", { name: "Versions published" })).toBeInTheDocument();
  });

  it("refuses values out of range before anything is sent", async () => {
    const api = fakeApi({ permissions: administrator });
    await renderAt("/settings/review-policies", api);

    fireEvent.change(await screen.findByRole("textbox", { name: /Second approval beyond/ }), { target: { value: "30" } });
    fireEvent.change(screen.getByRole("textbox", { name: /or beyond/ }), { target: { value: "5.5" } });

    expect(screen.getByText("0 to 24 hours, one decimal")).toBeInTheDocument();
    expect(screen.getByText("A whole number from 0 to 100")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Publish a new version" })).toBeDisabled();
    expect(api.writes()).toEqual([]);
  });

  it("shows the policy to someone who reads the queue, without changing it", async () => {
    await renderAt("/settings/review-policies", fakeApi({ permissions: reader }));

    expect(await screen.findByRole("textbox", { name: /Review within/ })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Publish a new version" })).not.toBeInTheDocument();
  });

  it.each([
    ["hours", "1,5", 1.5], ["hours", "24", 24], ["hours", "24.5", null], ["hours", "1.25", null],
    ["percent", "5", 0.05], ["percent", "100", 1], ["percent", "101", null], ["percent", "5.5", null],
    ["sla", "1", 1], ["sla", "168", 168], ["sla", "0", null], ["sla", "169", null],
  ] as const)("reads %s %s as %s", (field, text, expected) => {
    expect(policyInput[field](text)).toBe(expected);
  });
});

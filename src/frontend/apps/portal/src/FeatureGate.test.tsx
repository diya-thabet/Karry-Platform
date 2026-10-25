import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { ApiProvider } from "./ApiProvider";
import { FeatureGate } from "./FeatureGate";
import { fakeApi } from "./testing/fakeApi";

async function renderGate(features: string[]) {
  const api = fakeApi({ permissions: [], features });
  const i18n = await createI18n({ language: "en", pseudo: false });
  render(
    <I18nProvider i18n={i18n}>
      <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
        <ApiProvider client={api.client}>
          <FeatureGate feature="maintenance">
            <p>Work orders</p>
          </FeatureGate>
        </ApiProvider>
      </QueryClientProvider>
    </I18nProvider>,
  );
}

describe("FeatureGate (US-P1-BIL-03 AC2, TC-P1-BIL-003)", () => {
  it("shows the feature when the plan includes it", async () => {
    await renderGate(["maintenance"]);

    expect(await screen.findByText("Work orders")).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /not in your plan/ })).not.toBeInTheDocument();
  });

  it("shows the upgrade card, and never the feature, when the plan does not include it", async () => {
    await renderGate(["field-operations"]);

    expect(await screen.findByRole("heading", { name: "Maintenance is not in your plan" })).toBeInTheDocument();
    expect(screen.queryByText("Work orders")).not.toBeInTheDocument();
  });
});

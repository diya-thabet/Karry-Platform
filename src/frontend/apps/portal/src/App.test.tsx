import { QueryClientProvider, useQuery } from "@tanstack/react-query";
import { createMemoryHistory } from "@tanstack/react-router";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { ApiError } from "@karry/api-client";
import { AuthProvider } from "@karry/auth";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, I18nProvider, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "./App";
import { ErrorDialogProvider } from "./ErrorDialog";
import { createQueryClient } from "./queryClient";
import { useReportError } from "./reportError";
import { fakeApi } from "./testing/fakeApi";

async function renderPortal(options: { path?: string; pseudo?: boolean; language?: "fr" | "en" } = {}) {
  const i18n = await createI18n({ language: options.language ?? "fr", pseudo: options.pseudo ?? false });
  const history = createMemoryHistory({ initialEntries: [options.path ?? "/"] });
  const auth = await signedInAuth();
  render(<App i18n={i18n} auth={auth} api={fakeApi({ permissions: [] }).client} history={history} />);
  await screen.findByRole("navigation");
  return i18n;
}

describe("portal shell (US-P1-OFF-05/06, TC-P1-PLT-015)", () => {
  it("renders the layout in French by default with the main navigation", async () => {
    await renderPortal();

    expect(screen.getByText("Portail Karry")).toBeInTheDocument();
    const nav = screen.getByRole("navigation", { name: "Navigation principale" });
    expect(within(nav).getAllByRole("link").map((link) => link.textContent)).toEqual(["Accueil", "Sites", "Engins", "Audit", "Paramètres"]);
    expect(within(nav).getByRole("link", { name: "Accueil" })).toHaveAttribute("aria-current", "page");
    expect(screen.getByRole("main")).toBeInTheDocument();
  });

  it("switches language instantly: no reload, no remount (TC-P1-OFF-007)", async () => {
    const i18n = await renderPortal();
    const header = screen.getByText("Portail Karry");
    const href = window.location.href;

    await act(async () => { await i18n.changeLanguage("en"); });

    expect(screen.getByText("Karry Portal")).toBe(header); // same DOM node: re-rendered, not remounted
    expect(screen.getByRole("navigation", { name: "Main navigation" })).toBeInTheDocument();
    expect(window.location.href).toBe(href);
  });

  it("shows who is signed in and signs out through Keycloak (US-P1-IAM-01)", async () => {
    const i18n = await createI18n({ language: "fr", pseudo: false });
    const auth = await signedInAuth({ givenName: "Karim", familyName: "Trabelsi" });
    render(<App i18n={i18n} auth={auth} api={fakeApi({ permissions: [] }).client} history={createMemoryHistory({ initialEntries: ["/"] })} />);

    expect(await screen.findByText("Connecté : Karim Trabelsi")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Se déconnecter" }));

    await vi.waitFor(() => {
      expect(auth.signOuts).toBe(1);
    });
  });

  it("navigates between sections and marks the current one", async () => {
    await renderPortal();

    fireEvent.click(screen.getByRole("link", { name: "Engins" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Engins" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Engins" })).toHaveAttribute("aria-current", "page");
  });

  it("shows a not-found page for unknown addresses", async () => {
    await renderPortal({ path: "/nowhere" });

    expect(await screen.findByText("Cette page n’existe pas.")).toBeInTheDocument();
  });

  it("has no hard-coded text: in the pseudo-localized build every visible text is translated (TC-P1-OFF-007)", async () => {
    await renderPortal({ pseudo: true });

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no], script, style, option, select") !== null;
      if (text !== "" && !hidden && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });
});

describe("portal accessibility", () => {
  it("has no serious accessibility violation in its structure (contrast is checked in the browser suite)", async () => {
    await renderPortal();

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

describe("error dialog (US-P1-OPS-01 AC3)", () => {
  function Failing() {
    useQuery({
      queryKey: ["failing"],
      queryFn: () => Promise.reject(new ApiError(500, "server.unexpected_error", "4bf92f3577b34da6a3ce929d0e0e4736")),
    });
    return null;
  }

  function Harness() {
    const report = useReportError();
    return (
      <QueryClientProvider client={createQueryClient(report, false)}>
        <Failing />
      </QueryClientProvider>
    );
  }

  it("shows the support reference of a failed API call", async () => {
    const i18n = await createI18n({ language: "en" });
    const auth = await signedInAuth();
    render(<AuthProvider auth={auth}><I18nProvider i18n={i18n}><ErrorDialogProvider><Harness /></ErrorDialogProvider></I18nProvider></AuthProvider>);

    const dialog = await screen.findByRole("dialog", { name: "Something went wrong" });
    expect(within(dialog).getByTestId("correlation-id")).toHaveTextContent("4bf92f3577b34da6a3ce929d0e0e4736");
  });

  it("reads the correlation ID from the response header, or from problem details", async () => {
    const fromHeader = await ApiError.fromResponse(new Response(JSON.stringify({ code: "tenant.missing" }), { status: 403, headers: { "X-Correlation-Id": "abc" } }));
    const fromBody = await ApiError.fromResponse(new Response(JSON.stringify({ code: "x", traceId: "def" }), { status: 500 }));
    const notJson = await ApiError.fromResponse(new Response("oops", { status: 502 }));

    expect([fromHeader.status, fromHeader.code, fromHeader.correlationId]).toEqual([403, "tenant.missing", "abc"]);
    expect(fromBody.correlationId).toBe("def");
    expect([notJson.code, notJson.correlationId]).toEqual(["http.error", undefined]);
  });
});

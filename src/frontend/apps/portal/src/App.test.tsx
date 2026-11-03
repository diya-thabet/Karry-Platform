import { QueryClientProvider, useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { createMemoryHistory } from "@tanstack/react-router";
import { act, cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { ApiError } from "@karry/api-client";
import { AuthProvider } from "@karry/auth";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, I18nProvider, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "./App";
import { ErrorDialogProvider } from "./app/ErrorDialog";
import { createQueryClient } from "./app/queryClient";
import { useReportError } from "./app/reportError";
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

  it("mirrors the whole portal for Arabic and N'Ko, chosen from the account menu, and remembers the choice", async () => {
    localStorage.removeItem("karry.language");
    const i18n = await renderPortal();
    fireEvent.keyDown(screen.getByRole("button", { name: /^Connecté/ }), { key: "Enter" });
    fireEvent.keyDown(within(await screen.findByRole("menu")).getByRole("menuitem", { name: /^Langue/ }), { key: "ArrowRight" });
    fireEvent.click(await screen.findByRole("menuitemradio", { name: "العربية" }));

    await vi.waitFor(() => { expect([document.documentElement.lang, document.documentElement.dir]).toEqual(["ar", "rtl"]); }); // the catalog loads first
    expect(within(screen.getByRole("navigation", { name: "التنقل الرئيسي" })).getAllByRole("link").map((link) => link.textContent))
      .toEqual(["الرئيسية", "المواقع", "الآلات", "التدقيق", "الإعدادات"]);
    expect(localStorage.getItem("karry.language")).toBe("ar");
    fireEvent.keyDown(screen.getByRole("button", { name: /^مسجّل الدخول باسم/ }), { key: "Enter" });
    expect(await screen.findByRole("menu")).toHaveAttribute("dir", "rtl"); // menus and selects follow the direction
    fireEvent.keyDown(screen.getByRole("menu"), { key: "Escape" });

    await act(async () => { await i18n.changeLanguage("nqo"); });
    expect([document.documentElement.lang, document.documentElement.dir]).toEqual(["nqo", "rtl"]);
    expect(screen.getByRole("navigation", { name: "ߛߌߙߊ ߞߎ߲ߓߊ" })).toBeInTheDocument();

    await act(async () => { await i18n.changeLanguage("emk"); });
    expect([document.documentElement.lang, document.documentElement.dir]).toEqual(["emk", "ltr"]);
    expect(screen.getByRole("navigation", { name: "Sira kunba" })).toBeInTheDocument();
    const languages = await axe.run(document, { runOnly: ["html-has-lang", "html-lang-valid", "valid-lang"] });
    expect(languages.violations.map((v) => v.id)).toEqual([]);
    localStorage.removeItem("karry.language");
  });

  it("keeps the account menu short: language and theme show their value and open their choices (UX-01, UX-02)", async () => {
    localStorage.removeItem("karry.theme");
    await renderPortal();
    fireEvent.keyDown(screen.getByRole("button", { name: /^Connecté/ }), { key: "Enter" });
    const menu = await screen.findByRole("menu");

    // Three rows, not thirteen: each setting shows its current value; sign-out is last.
    expect(within(menu).getAllByRole("menuitem").map((item) => item.textContent)).toEqual(["LangueFrançais", "ThèmeClair", "Se déconnecter"]);
    expect(within(menu).queryByRole("menuitemradio")).not.toBeInTheDocument();

    fireEvent.keyDown(within(menu).getByRole("menuitem", { name: /^Thème/ }), { key: "ArrowRight" });
    const themes = await screen.findAllByRole("menuitemradio");
    expect(themes.map((item) => [item.textContent, item.getAttribute("aria-checked")])).toEqual([
      ["Clair", "true"], ["Sombre", "false"], ["Extérieur (contraste élevé)", "false"],
    ]); // screen readers hear which one is chosen
    fireEvent.click(screen.getByRole("menuitemradio", { name: "Sombre" }));

    expect(document.documentElement.dataset.theme).toBe("dark");
    expect(localStorage.getItem("karry.theme")).toBe("dark");
    localStorage.removeItem("karry.theme");
  });

  it("names each language in its own language, so a screen reader reads it with the right voice", async () => {
    await renderPortal();
    fireEvent.keyDown(screen.getByRole("button", { name: /^Connecté/ }), { key: "Enter" });
    fireEvent.keyDown(within(await screen.findByRole("menu")).getByRole("menuitem", { name: /^Langue/ }), { key: "ArrowRight" });

    const languages = await screen.findAllByRole("menuitemradio");
    expect(languages.map((item) => [item.textContent, item.getAttribute("lang")])).toEqual([
      ["Français", "fr"], ["English", "en"], ["العربية", "ar"], ["Maninkakan", "emk"], ["ߒߞߏ", "nqo"],
    ]);
  });

  it("shows who is signed in and signs out through Keycloak (US-P1-IAM-01)", async () => {
    const i18n = await createI18n({ language: "fr", pseudo: false });
    const auth = await signedInAuth({ givenName: "Karim", familyName: "Trabelsi" });
    render(<App i18n={i18n} auth={auth} api={fakeApi({ permissions: [] }).client} history={createMemoryHistory({ initialEntries: ["/"] })} />);

    // Who is signed in names the account menu; sign-out is in it.
    const account = await screen.findByRole("button", { name: "Connecté : Karim Trabelsi" });
    expect(account).toHaveTextContent("Karim Trabelsi");
    fireEvent.keyDown(account, { key: "Enter" });
    fireEvent.click(await screen.findByRole("menuitem", { name: "Se déconnecter" }));

    await vi.waitFor(() => {
      expect(auth.signOuts).toBe(1);
    });
  });

  it("reduces the sidebar to its icons on request and remembers it, links keeping their names", async () => {
    localStorage.removeItem("karry.sidebar");
    await renderPortal();

    fireEvent.click(screen.getByRole("button", { name: "Réduire le menu" }));

    expect(screen.getByRole("button", { name: "Garder le menu ouvert" })).toBeInTheDocument();
    expect(document.querySelector("aside")).toHaveAttribute("data-collapsed", "true");
    expect(localStorage.getItem("karry.sidebar")).toBe("collapsed");
    expect(screen.getByRole("link", { name: "Engins" })).toBeInTheDocument(); // the label stays for screen readers

    cleanup();
    await renderPortal();
    expect(document.querySelector("aside")).toHaveAttribute("data-collapsed", "true");
    fireEvent.click(screen.getByRole("button", { name: "Garder le menu ouvert" }));
    expect(localStorage.getItem("karry.sidebar")).toBe("expanded");
    expect(document.querySelector("aside")).toHaveAttribute("data-collapsed", "false");
  });

  it("opens the navigation as a drawer on a phone and closes it after choosing a screen", async () => {
    await renderPortal();

    fireEvent.click(screen.getByRole("button", { name: "Ouvrir le menu" }));
    const drawer = await screen.findByRole("dialog", { name: "Navigation principale" });
    fireEvent.click(within(drawer).getByRole("link", { name: "Engins" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Engins" })).toBeInTheDocument();
    await vi.waitFor(() => {
      expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
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

  it("explains a rate-limited request in the person's words and does not try it again at once (T-12)", async () => {
    const i18n = await createI18n({ language: "en" });
    const auth = await signedInAuth();
    const queryFn = vi.fn(() => Promise.reject(new ApiError(429, "request.rate_limited", "7a1c")));
    function Limited() {
      useQuery({ queryKey: ["limited"], queryFn, retryDelay: 0 });
      return null;
    }
    function LimitedHarness() {
      const report = useReportError();
      const [client] = useState(() => createQueryClient(report, 3));
      return <QueryClientProvider client={client}><Limited /></QueryClientProvider>;
    }
    render(<AuthProvider auth={auth}><I18nProvider i18n={i18n}><ErrorDialogProvider><LimitedHarness /></ErrorDialogProvider></I18nProvider></AuthProvider>);

    const dialog = await screen.findByRole("dialog");
    expect(dialog).toHaveTextContent("Too many requests in a short time. Wait a minute, then try again.");
    expect(queryFn).toHaveBeenCalledOnce();
  });

  it("still tries a query again after a server error", async () => {
    const client = createQueryClient(vi.fn(), 1);
    const queryFn = vi.fn(() => Promise.reject(new ApiError(503, "server.unavailable", undefined)));

    await expect(client.query({ queryKey: ["flaky"], queryFn, retryDelay: 0 })).rejects.toBeInstanceOf(ApiError);
    expect(queryFn).toHaveBeenCalledTimes(2);
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

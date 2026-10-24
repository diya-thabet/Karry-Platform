import { act, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "./App";

async function renderField(options: { pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: "fr", pseudo: options.pseudo ?? false });
  render(<App i18n={i18n} auth={await signedInAuth({ givenName: "Karim", familyName: "Trabelsi" })} />);
  return i18n;
}

describe("field shell (US-P1-OFF-01/05/06, TC-P1-PLT-015)", () => {
  it("shows the four field actions as large buttons, in the outdoor theme and field density", async () => {
    await renderField();

    const nav = screen.getByRole("navigation", { name: "Actions terrain" });
    expect(within(nav).getAllByRole("button").map((b) => b.textContent)).toEqual(["Carburant", "Compteur horaire", "Production", "Signaler un problème"]);
    expect(document.documentElement.dataset.theme).toBe("outdoor");
    expect(document.documentElement.dataset.density).toBe("field");
  });

  it("shows who is signed in with a large sign-out button (US-P1-IAM-01)", async () => {
    await renderField();

    expect(screen.getByText("Connecté : Karim Trabelsi")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Se déconnecter" })).toBeInTheDocument();
  });

  it("shows the synchronization status area (placeholder until the outbox, P1.15)", async () => {
    await renderField();

    expect(within(screen.getByRole("region", { name: "Synchronisation" })).getByText("État de synchronisation pas encore disponible")).toBeInTheDocument();
  });

  it("switches language instantly without remounting", async () => {
    const i18n = await renderField();
    const title = screen.getByRole("heading", { level: 1, name: "Karry Terrain" });

    await act(async () => { await i18n.changeLanguage("en"); });

    expect(screen.getByRole("heading", { level: 1, name: "Karry Field" })).toBe(title);
  });

  it("has no hard-coded text in the pseudo-localized build (TC-P1-OFF-007)", async () => {
    await renderField({ pseudo: true });

    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], option, select") !== null;
      if (text !== "" && !hidden && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });

  it("has no serious accessibility violation in its structure (contrast is checked in the browser suite)", async () => {
    await renderField();

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

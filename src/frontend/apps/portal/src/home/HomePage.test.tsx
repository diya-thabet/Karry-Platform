import { createMemoryHistory } from "@tanstack/react-router";
import { render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { App } from "../App";
import { fakeApi, site } from "../testing/fakeApi";

async function renderHome(api: ReturnType<typeof fakeApi>, options: { pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: "fr", pseudo: options.pseudo ?? false });
  const auth = await signedInAuth({ givenName: "Karim", familyName: "Trabelsi" });
  render(<App i18n={i18n} auth={auth} api={api.client} history={createMemoryHistory({ initialEntries: ["/"] })} />);
}

const sites = [
  site(),
  site({ id: "0192f0c1-0000-7000-8000-000000005002", code: "LAV-SUD", name: "Laverie Sud", type: "sand-washing" }),
  site({ id: "0192f0c1-0000-7000-8000-000000005003", code: "OLD", name: "Ancienne carrière", status: "archived" }),
];

describe("home page", () => {
  it("greets the person and counts what they may see, with the next things they may do", async () => {
    await renderHome(fakeApi({ permissions: ["organization.sites.read", "organization.sites.manage"], sites }));

    expect(await screen.findByRole("heading", { level: 1, name: "Bonjour, Karim" })).toBeInTheDocument();
    expect(await screen.findByText("Sites actifs")).toBeInTheDocument();
    expect(await screen.findByText("Laverie Sud")).toBeInTheDocument();
    expect(screen.queryByText("Ancienne carrière")).not.toBeInTheDocument(); // archived sites are not counted or listed
    expect(screen.getByText("Sites actifs").parentElement).toHaveTextContent("2");
    expect(screen.getByRole("link", { name: /Ajouter un site/ })).toBeInTheDocument();
    // Nothing the person may not read: no counts or actions for machines, materials or people.
    expect(screen.queryByText("Engins", { selector: "main *" })).not.toBeInTheDocument();
    expect(screen.queryByText("Utilisateurs")).not.toBeInTheDocument();
    expect(screen.queryByRole("link", { name: /Importer depuis Excel/ })).not.toBeInTheDocument();
  });

  it("welcomes a person who may see nothing yet", async () => {
    await renderHome(fakeApi({ permissions: [] }));

    const main = await screen.findByRole("main");
    expect(await within(main).findByText(/^Bienvenue chez /)).not.toHaveTextContent("{company}");
    expect(within(main).queryByText("Sites actifs")).not.toBeInTheDocument();
  });

  it("has no hard-coded text (TC-P1-OFF-007)", async () => {
    await renderHome(fakeApi({ permissions: ["organization.sites.read", "organization.sites.manage", "identity.users.read"], sites }), { pseudo: true });
    await screen.findByText("Laverie Sud");

    const walker = document.createTreeWalker(screen.getByRole("main"), NodeFilter.SHOW_TEXT);
    const untranslated: string[] = [];
    for (let node = walker.nextNode(); node; node = walker.nextNode()) {
      const text = node.textContent?.trim() ?? "";
      const hidden = node.parentElement?.closest("[aria-hidden=true], [translate=no]") !== null;
      const data = /^(\d+\+?|…)$/.test(text); // counts
      if (text !== "" && !hidden && !data && !text.includes(pseudoOpen) && !text.includes(pseudoClose)) {
        untranslated.push(text);
      }
    }

    expect(untranslated).toEqual([]);
  });

  it("has no serious accessibility violation", async () => {
    await renderHome(fakeApi({ permissions: ["organization.sites.read", "organization.sites.manage"], sites }));
    await screen.findByText("Laverie Sud");

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

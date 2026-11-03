import { createMemoryHistory } from "@tanstack/react-router";
import { render, screen, within } from "@testing-library/react";
import { signedInAuth } from "@karry/auth/testing";
import { createI18n } from "@karry/i18n";
import { App } from "../App";
import { asset, fakeApi, material, site } from "../testing/fakeApi";

// Every permission a company administrator holds, so every screen opens.
const everything = [
  "identity.roles.manage", "identity.roles.read", "identity.users.manage", "identity.users.read", "identity.devices.manage", "identity.support.manage",
  "billing.subscription.read", "tenancy.settings.manage", "organization.sites.read", "organization.sites.manage", "assets.registry.read",
  "assets.registry.manage", "materials.catalog.read", "materials.catalog.manage", "imports.files.manage", "audit.ledger.read",
];

async function renderAt(path: string, api: ReturnType<typeof fakeApi>["client"] = fakeApi({ permissions: everything }).client) {
  const i18n = await createI18n({ language: "fr" });
  render(<App i18n={i18n} auth={await signedInAuth()} api={api} history={createMemoryHistory({ initialEntries: [path] })} />);
}

/** The trail above the page's title: the names of its links, in order. */
async function trail() {
  const nav = await screen.findByRole("navigation", { name: "Fil d’Ariane" });
  return within(nav).getAllByRole("link").map((link) => [link.textContent, link.getAttribute("href")]);
}

describe("wayfinding: every page below a section says where it is (UX-04, UX-05, UX-06)", () => {
  it.each([
    ["/settings/company", "Entreprise"],
    ["/settings/roles", "Rôles"],
    ["/settings/users", "Personnes"],
    ["/settings/tablets", "Tablettes de site"],
    ["/settings/materials", "Matériaux"],
    ["/settings/imports", "Import depuis Excel"],
    ["/settings/support", "Accès support"],
    ["/settings/plan", "Abonnement et utilisation"],
  ])("%s: Paramètres above the title", async (path, title) => {
    await renderAt(path);

    expect(await screen.findByRole("heading", { level: 1, name: title })).toBeInTheDocument();
    expect(await trail()).toEqual([["Paramètres", "/settings"]]);
    expect(screen.getAllByRole("heading", { level: 1 })).toHaveLength(1);
  });

  it("puts a site under Sites", async () => {
    const nord = site();
    const api = fakeApi({ permissions: everything, sites: [nord] });

    await renderAt(`/sites/${nord.id}`, api.client);
    expect(await screen.findByRole("heading", { level: 1, name: nord.name })).toBeInTheDocument();
    expect(await trail()).toEqual([["Sites", "/sites"]]);
  });

  it.each([
    ["machine", (ids: { asset: string }) => `/assets/${ids.asset}`, [["Engins", "/assets"]]],
    ["material", (ids: { material: string }) => `/settings/materials/${ids.material}`, [["Paramètres", "/settings"], ["Matériaux", "/settings/materials"]]],
  ] as const)("puts a %s under its list", async (_kind, path, expected) => {
    const nord = site();
    const loader = asset({ siteId: nord.id });
    const gravel = material();
    const api = fakeApi({ permissions: everything, sites: [nord], assets: [loader], materials: [gravel] });

    await renderAt(path({ asset: loader.id, material: gravel.id }), api.client);
    expect(await trail()).toEqual(expected);
  });

  it("gives the top sections no trail: they are in the sidebar", async () => {
    await renderAt("/sites");

    expect(await screen.findByRole("heading", { level: 1, name: "Sites" })).toBeInTheDocument();
    expect(screen.queryByRole("navigation", { name: "Fil d’Ariane" })).not.toBeInTheDocument();
  });
});

describe("loading is never shown as empty (UX-12, UX-13)", () => {
  /** The API, except that the given address never answers (a slow network). */
  function slow(path: string) {
    const { client } = fakeApi({ permissions: everything, sites: [site()] });
    return { ...client, GET: ((url: string, init: unknown) => (url === path ? new Promise(() => undefined) : client.GET(url as never, init as never))) as typeof client.GET };
  }

  it("a list keeps its shape with skeleton rows, announced as busy, and no empty state", async () => {
    await renderAt("/sites", slow("/api/v1/organization/sites"));

    const table = await screen.findByRole("table", { name: "Sites" });
    expect(table).toHaveAttribute("aria-busy", "true");
    expect(within(table).getByText("Chargement…")).toBeInTheDocument();
    expect(screen.queryByText("Aucun site pour l’instant")).not.toBeInTheDocument();
  });

  it("a record's page shows its skeleton, not a blank page", async () => {
    await renderAt("/sites/0192f0c1-0000-7000-8000-00000000beef", slow("/api/v1/organization/sites/{siteId}"));

    const loading = await screen.findByRole("status");
    expect(loading).toHaveAttribute("aria-busy", "true");
    expect(loading).toHaveTextContent("Chargement…");
  });
});

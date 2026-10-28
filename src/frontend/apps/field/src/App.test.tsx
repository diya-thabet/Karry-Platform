import "fake-indexeddb/auto";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { applyChanges, openOfflineDb, type DeviceRecord } from "@karry/offline";
import { App, type FieldEnvironment } from "./App";
import { createUpdates } from "./updates";

async function device(): Promise<DeviceRecord> {
  const keys = await generateDeviceKey();
  return {
    id: "this", deviceId: "d1", tenantId: "t1", siteId: "s1", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32)), enrolledAt: "2026-10-28T10:00:00Z", clockOffsetMs: 0,
  };
}

function environment(overrides: Partial<FieldEnvironment> = {}): FieldEnvironment & { updates: ReturnType<typeof createUpdates> } {
  return {
    db: openOfflineDb(`field-${crypto.randomUUID()}`), updates: createUpdates(), persisted: Promise.resolve(true),
    company: "demo-carriere-a", code: null, enroll: vi.fn(device), pull: vi.fn(() => Promise.resolve("ok" as const)), ...overrides,
  } as FieldEnvironment & { updates: ReturnType<typeof createUpdates> };
}

async function enrolled(overrides: Partial<FieldEnvironment> = {}) {
  const env = environment(overrides);
  await env.db.device.put(await device());
  return env;
}

async function renderField(env: FieldEnvironment, options: { pseudo?: boolean } = {}) {
  const i18n = await createI18n({ language: "fr", pseudo: options.pseudo ?? false });
  render(<App i18n={i18n} environment={env} />);
  return i18n;
}

describe("enrollment screen (US-P1-OFF-02 AC1)", () => {
  it("opens on a tablet without a device key, with the code of the scanned link filled in", async () => {
    await renderField(environment({ code: "K7QM-2XRA-9D4T-WB3N" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Enregistrer cette tablette" })).toBeInTheDocument();
    expect(screen.getByLabelText(/Code d’enregistrement/)).toHaveValue("K7QM-2XRA-9D4T-WB3N");
    expect(screen.queryByLabelText(/Entreprise/)).not.toBeInTheDocument();
  });

  it("enrols with the typed code and name, then shows the home screen", async () => {
    const env = environment();
    await renderField(env);

    fireEvent.change(await screen.findByLabelText(/Code d’enregistrement/), { target: { value: "k7qm2xra9d4twb3n" } });
    fireEvent.change(screen.getByLabelText(/Nom de la tablette/), { target: { value: "Pont-bascule entrée" } });
    fireEvent.click(screen.getByRole("button", { name: "Enregistrer la tablette" }));

    expect(env.enroll).toHaveBeenCalledWith({ company: "demo-carriere-a", code: "k7qm2xra9d4twb3n", name: "Pont-bascule entrée" });
    expect(await screen.findByRole("heading", { level: 1, name: "Karry Terrain" })).toBeInTheDocument();
  });

  it("asks for the company on an address that does not name it", async () => {
    const env = environment({ company: null });
    await renderField(env);

    fireEvent.change(await screen.findByLabelText(/Entreprise/), { target: { value: "demo-carriere-b" } });
    fireEvent.change(screen.getByLabelText(/Code d’enregistrement/), { target: { value: "K7QM2XRA9D4TWB3N" } });
    fireEvent.change(screen.getByLabelText(/Nom de la tablette/), { target: { value: "Atelier" } });
    fireEvent.click(screen.getByRole("button", { name: "Enregistrer la tablette" }));

    expect(env.enroll).toHaveBeenCalledWith(expect.objectContaining({ company: "demo-carriere-b" }));
  });

  it("explains a refused code and stays on the screen (TC-P1-OFF-002)", async () => {
    await renderField(environment({ code: "K7QM-2XRA-9D4T-WB3N", enroll: vi.fn(() => Promise.resolve("expired" as const)) }));

    fireEvent.change(await screen.findByLabelText(/Nom de la tablette/), { target: { value: "Atelier" } });
    fireEvent.click(screen.getByRole("button", { name: "Enregistrer la tablette" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Ce code a expiré ou a déjà servi. Demandez un nouveau code.");
    expect(screen.getByRole("button", { name: "Enregistrer la tablette" })).toBeEnabled();
  });

  it("never offers an update in the middle of the enrollment form (US-P1-OFF-01 AC2)", async () => {
    const env = environment();
    await renderField(env);
    await screen.findByRole("heading", { level: 1, name: "Enregistrer cette tablette" });

    act(() => { env.updates.offer(vi.fn(() => Promise.resolve())); });

    expect(screen.queryByRole("button", { name: "Mettre à jour" })).not.toBeInTheDocument();
  });
});

describe("field home (US-P1-OFF-01/05/06, TC-P1-PLT-015)", () => {
  it("shows the four field actions as large buttons, in the outdoor theme and field density", async () => {
    await renderField(await enrolled());

    const nav = await screen.findByRole("navigation", { name: "Actions terrain" });
    expect(within(nav).getAllByRole("button").map((b) => b.textContent)).toEqual(["Carburant", "Compteur horaire", "Production", "Signaler un problème"]);
    expect(document.documentElement.dataset.theme).toBe("outdoor");
    expect(document.documentElement.dataset.density).toBe("field");
  });

  it("offers a downloaded version on the home screen and installs it only when asked (US-P1-OFF-01 AC2)", async () => {
    const env = await enrolled();
    const apply = vi.fn(() => Promise.resolve());
    await renderField(env);
    await screen.findByRole("heading", { level: 1, name: "Karry Terrain" });
    expect(screen.queryByRole("region", { name: "Mise à jour" })).not.toBeInTheDocument();

    act(() => { env.updates.offer(apply); });
    expect(apply).not.toHaveBeenCalled();
    fireEvent.click(within(screen.getByRole("region", { name: "Mise à jour" })).getByRole("button", { name: "Mettre à jour" }));

    expect(apply).toHaveBeenCalledOnce();
  });

  it("warns when the browser does not guarantee the tablet's storage (US-P1-OFF-01 AC3)", async () => {
    await renderField(await enrolled({ persisted: Promise.resolve(false) }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Stockage non garanti");
  });

  it("shows no storage warning when storage is kept", async () => {
    await renderField(await enrolled());
    await screen.findByRole("heading", { level: 1, name: "Karry Terrain" });

    expect(screen.queryByRole("alert")).not.toBeInTheDocument();
  });

  it("downloads the site's data on opening and shows the site, the refresh time and what is kept offline (US-P1-OFF-03)", async () => {
    const env = await enrolled();
    env.pull = vi.fn(async () => {
      await applyChanges(env.db, {
        full: true, cursor: "c1", serverTime: "2026-10-28T13:05:00Z",
        changes: {
          sites: { upserts: [{ id: "s1", code: "NORD", name: "Carrière Nord", type: "quarry", timeZone: "Africa/Tunis" }], removes: [] },
          shiftPatterns: { upserts: [], removes: [] },
          assets: { upserts: [{ id: "a1", code: "PE-01", name: "Pelle", type: "excavator", siteId: "s1", status: "active", make: null, model: null, serialNumber: null, plate: null, labelCode: null }], removes: [] },
          meters: { upserts: [], removes: [] },
          materials: { upserts: [], removes: [] },
          users: { upserts: [{ id: "u1", employeeId: "OP-1", name: "Karim", pin: null }, { id: "u2", employeeId: "OP-2", name: "Sami", pin: null }], removes: [] },
        },
      });
      return "ok" as const;
    });
    await renderField(env);

    const region = await screen.findByRole("region", { name: "Synchronisation" });
    expect(await within(region).findByText("À jour")).toBeInTheDocument();
    expect(within(region).getByText("Site : NORD · Carrière Nord")).toBeInTheDocument();
    expect(within(region).getByText(/^Données mises à jour le 28 oct. 2026 à \d{2}:05$/)).toBeInTheDocument();
    expect(within(region).getByText("1 machine et 2 opérateurs disponibles hors ligne")).toBeInTheDocument();
    expect(env.pull).toHaveBeenCalledOnce();
  });

  it("refreshes on request and says so when the tablet is offline before the first download", async () => {
    const env = await enrolled({ pull: vi.fn(() => Promise.resolve("offline" as const)) });
    await renderField(env);

    const region = await screen.findByRole("region", { name: "Synchronisation" });
    expect(await within(region).findByText("Hors ligne")).toBeInTheDocument();
    expect(within(region).getByText(/pas encore téléchargées/)).toBeInTheDocument();
    fireEvent.click(within(region).getByRole("button", { name: "Actualiser" }));

    await vi.waitFor(() => { expect(env.pull).toHaveBeenCalledTimes(2); });
  });

  it("refreshes when the network comes back", async () => {
    const env = await enrolled();
    await renderField(env);
    await screen.findByText("À jour");

    act(() => { window.dispatchEvent(new Event("online")); });

    await vi.waitFor(() => { expect(env.pull).toHaveBeenCalledTimes(2); });
  });

  it("tells a revoked tablet that it no longer receives data (TC-P1-OFF-005)", async () => {
    await renderField(await enrolled({ pull: vi.fn(() => Promise.resolve("revoked" as const)) }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Cette tablette a été révoquée dans le portail.");
    expect(screen.queryByRole("button", { name: "Actualiser" })).not.toBeInTheDocument();
  });

  it("switches language instantly without remounting", async () => {
    const i18n = await renderField(await enrolled());
    const title = await screen.findByRole("heading", { level: 1, name: "Karry Terrain" });

    await act(async () => { await i18n.changeLanguage("en"); });

    expect(screen.getByRole("heading", { level: 1, name: "Karry Field" })).toBe(title);
  });
});

describe("every screen (TC-P1-OFF-007, OFF-05)", () => {
  const screens = [
    ["enrollment", () => Promise.resolve(environment({ company: null, persisted: Promise.resolve(false) })), "Enregistrer cette tablette"],
    ["home", () => enrolled({ persisted: Promise.resolve(false) }), "Karry Terrain"],
  ] as const;

  it.each(screens)("%s has no hard-coded text in the pseudo-localized build", async (_, make) => {
    await renderField(await make(), { pseudo: true });
    await screen.findByRole("heading", { level: 1 });

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

  it.each(screens)("%s has no serious accessibility violation in its structure (contrast is checked in the browser suite)", async (_, make, title) => {
    await renderField(await make());
    await screen.findByRole("heading", { level: 1, name: title });

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

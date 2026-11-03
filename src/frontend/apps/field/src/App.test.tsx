import "fake-indexeddb/auto";
import { act, fireEvent, render, screen, within } from "@testing-library/react";
import axe from "axe-core";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { createI18n, pseudoClose, pseudoOpen } from "@karry/i18n";
import { applyChanges, openOfflineDb, type DeviceRecord } from "@karry/offline";
import { App, type FieldEnvironment } from "./App";
import { nextRefreshMs, refreshEveryMs } from "./sync";
import type { Session } from "./unlock";
import { createUpdates } from "./updates";

const operator: Session = { userId: "u1", name: "Karim Ben Ali", employeeId: "OP-1", checkedBy: "server" };

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
    company: "demo-carriere-a", code: null, enroll: vi.fn(device), pull: vi.fn(() => Promise.resolve("ok" as const)),
    unlock: vi.fn(() => Promise.resolve(operator)), scanner: null, ...overrides,
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

function unlockAs(employeeId: string, pin: string) {
  fireEvent.change(screen.getByLabelText(/Matricule/), { target: { value: employeeId } });
  fireEvent.change(screen.getByLabelText(/Code PIN/), { target: { value: pin } });
  fireEvent.click(screen.getByRole("button", { name: "Déverrouiller" }));
}

/** The home screen of an enrolled tablet, once someone unlocked it. */
async function renderHome(env: FieldEnvironment, options: { pseudo?: boolean } = {}) {
  const i18n = await renderField(env, options);
  await screen.findByRole("heading", { level: 1 });
  const { t } = i18n;
  fireEvent.change(screen.getByLabelText(t("field.unlock.employeeId"), { exact: false }), { target: { value: "OP-1" } });
  fireEvent.change(screen.getByLabelText(t("field.unlock.pin"), { exact: false }), { target: { value: "804613" } });
  fireEvent.click(screen.getByRole("button", { name: t("field.unlock.submit") }));
  await screen.findByRole("navigation");
  return i18n;
}

describe("enrollment screen (US-P1-OFF-02 AC1)", () => {
  it("opens on a tablet without a device key, with the code of the scanned link filled in", async () => {
    await renderField(environment({ code: "K7QM-2XRA-9D4T-WB3N" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Enregistrer cette tablette" })).toBeInTheDocument();
    expect(screen.getByLabelText(/Code d’enregistrement/)).toHaveValue("K7QM-2XRA-9D4T-WB3N");
    expect(screen.queryByLabelText(/Entreprise/)).not.toBeInTheDocument();
  });

  it("enrols with the typed code and name, then asks who uses the tablet", async () => {
    const env = environment();
    await renderField(env);

    fireEvent.change(await screen.findByLabelText(/Code d’enregistrement/), { target: { value: "k7qm2xra9d4twb3n" } });
    fireEvent.change(screen.getByLabelText(/Nom de la tablette/), { target: { value: "Pont-bascule entrée" } });
    fireEvent.click(screen.getByRole("button", { name: "Enregistrer la tablette" }));

    expect(env.enroll).toHaveBeenCalledWith({ company: "demo-carriere-a", code: "k7qm2xra9d4twb3n", name: "Pont-bascule entrée" });
    expect(await screen.findByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeInTheDocument();
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

describe("shared tablet (US-P1-OFF-04)", () => {
  it("opens locked, unlocks with the employee ID and PIN, and shows who has the tablet", async () => {
    const env = await enrolled();
    await renderField(env);
    expect(await screen.findByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeInTheDocument();
    expect(screen.getByLabelText(/Code PIN/)).toHaveAttribute("type", "password");
    expect(screen.queryByRole("navigation")).not.toBeInTheDocument();

    unlockAs("OP-1", "804613");

    expect(await screen.findByText("Karim Ben Ali (OP-1)")).toBeInTheDocument();
    expect(env.unlock).toHaveBeenCalledWith("OP-1", "804613");
  });

  it.each([
    ["wrong", "Matricule ou code PIN incorrect."],
    ["locked", "Trop de codes PIN incorrects."],
    ["online", "Cette tablette a besoin d’une connexion pour vous reconnaître"],
    ["revoked", "Cette tablette a été révoquée dans le portail."],
  ] as const)("explains a refused unlock (%s), clears the PIN and keeps the employee ID", async (failure, text) => {
    await renderField(await enrolled({ unlock: vi.fn(() => Promise.resolve(failure)) }));
    await screen.findByRole("heading", { level: 1, name: "Déverrouiller la tablette" });

    unlockAs("OP-1", "000000");

    expect(await screen.findByRole("alert")).toHaveTextContent(text);
    expect(screen.getByLabelText(/Code PIN/)).toHaveValue("");
    expect(screen.getByLabelText(/Matricule/)).toHaveValue("OP-1");
    expect(screen.queryByRole("navigation")).not.toBeInTheDocument();
  });

  it("locks on request, so the next person unlocks with their own PIN", async () => {
    await renderHome(await enrolled());

    fireEvent.click(screen.getByRole("button", { name: "Verrouiller" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeInTheDocument();
    expect(screen.queryByText("Karim Ben Ali (OP-1)")).not.toBeInTheDocument();
  });

  it("locks itself after the server's idle time, and each touch starts it again (AC3)", async () => {
    const env = await enrolled();
    await env.db.settings.put({ id: "this", autoLockMinutes: 2, offlineSessionHours: 72 });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      await renderHome(env);
      act(() => { vi.advanceTimersByTime(90_000); });
      fireEvent.pointerDown(window);
      act(() => { vi.advanceTimersByTime(90_000); });
      expect(screen.getByRole("navigation")).toBeInTheDocument();

      act(() => { vi.advanceTimersByTime(45_000); });
      expect(screen.getByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("locks when the tablet wakes up past the idle time", async () => {
    await renderHome(await enrolled());
    vi.useFakeTimers({ shouldAdvanceTime: true, toFake: ["Date"] });
    try {
      vi.setSystemTime(Date.now() + 6 * 60_000);
      act(() => { document.dispatchEvent(new Event("visibilitychange")); });

      expect(screen.getByRole("heading", { level: 1, name: "Déverrouiller la tablette" })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});

describe("finding a machine (P1.15, ADR-0028 §5)", () => {
  const label = "KRY1:ABCDEFGHIJKLMNOPQRSTUVWXYZ";

  async function withMachines(overrides: Partial<FieldEnvironment> = {}) {
    const env = await enrolled(overrides);
    const machine = (id: string, code: string, name: string, labelCode: string | null) => ({
      id, code, name, type: "excavator", siteId: "s1", status: "active", make: "Caterpillar", model: "336", serialNumber: null, plate: "123 TU 4567", labelCode,
    });
    await applyChanges(env.db, {
      full: true, cursor: "c1", serverTime: "2026-10-28T10:00:00Z",
      changes: {
        sites: { upserts: [], removes: [] }, shiftPatterns: { upserts: [], removes: [] }, meters: { upserts: [], removes: [] },
        materials: { upserts: [], removes: [] }, users: { upserts: [], removes: [] },
        assets: { upserts: [machine("a1", "PE-01", "Pelle 1", label), machine("a2", "PE-02", "Pelle 2", null), machine("t1", "TO-01", "Tombereau", null)], removes: [] },
      },
    });
    return env;
  }

  /** A camera that reads this text once it is started, and says when it is released. */
  function camera(text: string) {
    const released = vi.fn();
    const scanner = { start: vi.fn((_: HTMLVideoElement, onCode: (code: string) => void) => {
      setTimeout(() => { onCode(text); }, 0);
      return Promise.resolve(released);
    }) };
    return { scanner, released };
  }

  async function openFinder(env: FieldEnvironment) {
    await renderHome(env);
    fireEvent.click(screen.getByRole("button", { name: "Trouver une machine" }));
    await screen.findByRole("heading", { level: 1, name: "Trouver une machine" });
  }

  it("selects the machine of a scanned QR label and releases the camera", async () => {
    const { scanner, released } = camera(label);
    await openFinder(await withMachines({ scanner }));

    fireEvent.click(screen.getByRole("button", { name: "Scanner l’étiquette QR" }));

    const card = await screen.findByRole("region", { name: "Machine choisie" });
    expect(card).toHaveTextContent("PE-01");
    expect(card).toHaveTextContent("Pelle 1");
    expect(card).toHaveTextContent("Caterpillar · 336 · 123 TU 4567");
    await vi.waitFor(() => { expect(released).toHaveBeenCalled(); });
    expect(screen.queryByLabelText(/Caméra/)).not.toBeInTheDocument();
  });

  it("says when a scanned label is not one of this tablet's machines", async () => {
    await openFinder(await withMachines({ scanner: camera("KRY1:QQQQQQQQQQQQQQQQQQQQQQQQQQ").scanner }));

    fireEvent.click(screen.getByRole("button", { name: "Scanner l’étiquette QR" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Étiquette inconnue sur cette tablette");
    expect(screen.queryByRole("region", { name: "Machine choisie" })).not.toBeInTheDocument();
  });

  it("explains a camera that cannot open and keeps the typed search", async () => {
    await openFinder(await withMachines({ scanner: { start: vi.fn(() => Promise.reject(new DOMException("denied", "NotAllowedError"))) } }));

    fireEvent.click(screen.getByRole("button", { name: "Scanner l’étiquette QR" }));

    expect(await screen.findByRole("alert")).toHaveTextContent("La caméra n’est pas disponible.");
    expect(screen.getByLabelText(/Code, nom ou étiquette/)).toBeInTheDocument();
  });

  it("finds a machine by its typed code on a tablet without a QR reader, then goes back home", async () => {
    await openFinder(await withMachines());
    expect(screen.queryByRole("button", { name: "Scanner l’étiquette QR" })).not.toBeInTheDocument();
    expect(screen.getByText(/Cette tablette ne lit pas les QR codes/)).toBeInTheDocument();

    fireEvent.change(screen.getByLabelText(/Code, nom ou étiquette/), { target: { value: "pe" } });
    const results = await screen.findByRole("list", { name: "Machines trouvées" });
    expect(within(results).getAllByRole("button").map((b) => b.textContent)).toEqual(["PE-01Pelle 1", "PE-02Pelle 2"]);
    fireEvent.click(within(results).getByRole("button", { name: /PE-02/ }));
    expect(await screen.findByRole("region", { name: "Machine choisie" })).toHaveTextContent("Pelle 2");

    fireEvent.change(screen.getByLabelText(/Code, nom ou étiquette/), { target: { value: "grue" } });
    expect(await screen.findByText("Aucune machine de ce site ne correspond.")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Retour à l’accueil" }));
    expect(await screen.findByRole("heading", { level: 1, name: "Karry Terrain" })).toBeInTheDocument();
  });
});

describe("field home (US-P1-OFF-01/05/06, TC-P1-PLT-015)", () => {
  it("shows the four field actions as large buttons, not usable before their phase (UX-16), in the outdoor theme and field density", async () => {
    await renderHome(await enrolled());

    const nav = await screen.findByRole("navigation", { name: "Actions terrain" });
    const actions = within(nav).getAllByRole("button");
    expect(actions.map((b) => b.textContent)).toEqual(["CarburantBientôt disponible", "Compteur horaireBientôt disponible", "ProductionBientôt disponible", "Signaler un problèmeBientôt disponible"]);
    for (const action of actions) {
      expect(action).toBeDisabled();
    }
    expect(document.documentElement.dataset.theme).toBe("outdoor");
    expect(document.documentElement.dataset.density).toBe("field");
  });

  it("offers a downloaded version on the home screen and installs it only when asked (US-P1-OFF-01 AC2)", async () => {
    const env = await enrolled();
    const apply = vi.fn(() => Promise.resolve());
    await renderHome(env);
    await screen.findByRole("heading", { level: 1, name: "Karry Terrain" });
    expect(screen.queryByRole("region", { name: "Mise à jour" })).not.toBeInTheDocument();

    act(() => { env.updates.offer(apply); });
    expect(apply).not.toHaveBeenCalled();
    fireEvent.click(within(screen.getByRole("region", { name: "Mise à jour" })).getByRole("button", { name: "Mettre à jour" }));

    expect(apply).toHaveBeenCalledOnce();
  });

  it("warns when the browser does not guarantee the tablet's storage (US-P1-OFF-01 AC3)", async () => {
    await renderHome(await enrolled({ persisted: Promise.resolve(false) }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Stockage non garanti");
  });

  it("shows no storage warning when storage is kept", async () => {
    await renderHome(await enrolled());
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
    await renderHome(env);

    const region = await screen.findByRole("region", { name: "Synchronisation" });
    expect(await within(region).findByText("À jour")).toBeInTheDocument();
    expect(within(region).getByText("Site : NORD · Carrière Nord")).toBeInTheDocument();
    expect(within(region).getByText(/^Données mises à jour le 28 oct. 2026 à \d{2}:05$/)).toBeInTheDocument();
    expect(within(region).getByText("1 machine et 2 opérateurs disponibles hors ligne")).toBeInTheDocument();
    expect(env.pull).toHaveBeenCalledOnce();
  });

  it("refreshes on request and says so when the tablet is offline before the first download", async () => {
    const env = await enrolled({ pull: vi.fn(() => Promise.resolve("offline" as const)) });
    await renderHome(env);

    const region = await screen.findByRole("region", { name: "Synchronisation" });
    expect(await within(region).findByText("Hors ligne")).toBeInTheDocument();
    expect(within(region).getByText(/pas encore téléchargées/)).toBeInTheDocument();
    fireEvent.click(within(region).getByRole("button", { name: "Actualiser" }));

    await vi.waitFor(() => { expect(env.pull).toHaveBeenCalledTimes(2); });
  });

  it("refreshes when the network comes back", async () => {
    const env = await enrolled();
    await renderHome(env);
    await screen.findByText("À jour");

    act(() => { window.dispatchEvent(new Event("online")); });

    await vi.waitFor(() => { expect(env.pull).toHaveBeenCalledTimes(2); });
  });

  it("waits when the server is busy and tries again on its own, later than the server asked (T-12)", async () => {
    const pull = vi.fn<FieldEnvironment["pull"]>().mockResolvedValueOnce("busy").mockResolvedValue("ok");
    const env = await enrolled({ pull });
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      await renderHome(env);
      const region = await screen.findByRole("region", { name: "Synchronisation" });
      expect(await within(region).findByText("Serveur occupé, nouvel essai bientôt")).toBeInTheDocument();

      act(() => { vi.advanceTimersByTime(59_000); });
      expect(pull).toHaveBeenCalledOnce();
      act(() => { vi.advanceTimersByTime(32_000); });
      await vi.waitFor(() => { expect(pull).toHaveBeenCalledTimes(2); });
      expect(await within(region).findByText("À jour")).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it("spreads the tablets' refreshes around five minutes, and waits 60 to 90 seconds after a 429", () => {
    expect(nextRefreshMs("ok", () => 0)).toBe(refreshEveryMs - 30_000);
    expect(nextRefreshMs("ok", () => 1)).toBe(refreshEveryMs + 30_000);
    expect(nextRefreshMs("failed", () => 0.5)).toBe(refreshEveryMs);
    expect(nextRefreshMs("busy", () => 0)).toBe(60_000);
    expect(nextRefreshMs("busy", () => 1)).toBe(90_000);
    const moments = new Set(Array.from({ length: 50 }, () => nextRefreshMs("ok")));
    expect(moments.size).toBeGreaterThan(40);
  });

  it("tells a revoked tablet that it no longer receives data (TC-P1-OFF-005)", async () => {
    await renderHome(await enrolled({ pull: vi.fn(() => Promise.resolve("revoked" as const)) }));

    expect(await screen.findByRole("alert")).toHaveTextContent("Cette tablette a été révoquée dans le portail.");
    expect(screen.queryByRole("button", { name: "Actualiser" })).not.toBeInTheDocument();
  });

  it("switches language instantly without remounting", async () => {
    const i18n = await renderHome(await enrolled());
    const title = await screen.findByRole("heading", { level: 1, name: "Karry Terrain" });

    await act(async () => { await i18n.changeLanguage("en"); });

    expect(screen.getByRole("heading", { level: 1, name: "Karry Field" })).toBe(title);
  });
});

describe("every screen (TC-P1-OFF-007, OFF-05)", () => {
  const screens = [
    ["enrollment", () => Promise.resolve(environment({ company: null, persisted: Promise.resolve(false) })), "Enregistrer cette tablette"],
    ["lock", () => enrolled(), "Déverrouiller la tablette"],
    ["home", () => enrolled({ persisted: Promise.resolve(false) }), "Karry Terrain"],
    ["machine finder", () => enrolled(), "Trouver une machine"],
  ] as const;

  /** Opens the screen as a person would reach it. */
  async function open(name: (typeof screens)[number][0], env: FieldEnvironment, options: { pseudo?: boolean } = {}) {
    if (name === "enrollment" || name === "lock") {
      return renderField(env, options);
    }

    const i18n = await renderHome(env, options);
    if (name === "machine finder") {
      fireEvent.click(screen.getByRole("button", { name: i18n.t("field.machines.open") }));
    }

    return i18n;
  }

  it.each(screens)("%s has no hard-coded text in the pseudo-localized build", async (name, make) => {
    await open(name, await make(), { pseudo: true });
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

  it.each(screens)("%s has no serious accessibility violation in its structure (contrast is checked in the browser suite)", async (name, make, title) => {
    await open(name, await make());
    await screen.findByRole("heading", { level: 1, name: title });

    const result = await axe.run(document.body, { rules: { "color-contrast": { enabled: false } } });
    expect(result.violations.filter((v) => v.impact === "serious" || v.impact === "critical").map((v) => v.id)).toEqual([]);
  });
});

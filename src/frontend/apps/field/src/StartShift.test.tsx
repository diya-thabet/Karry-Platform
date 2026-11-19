import "fake-indexeddb/auto";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { createI18n, I18nProvider } from "@karry/i18n";
import { openOfflineDb, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";
import type { Camera, Photo } from "./camera";
import { checklistCommand } from "./checklistRules";
import { startCommand } from "./meterState";
import { StartShift, type FormEnvironment } from "./StartShift";

// The pad is drawn with a finger: in the test a button hands over a signature as the pad would.
vi.mock("./SignStep", () => ({
  default: ({ onConfirm, summary }: { onConfirm: (s: { strokesJson: string; png: Uint8Array }) => Promise<void>; summary: unknown }) => (
    <section aria-label="pad">
      <div>{summary as never}</div>
      <button type="button" onClick={() => { void onConfirm({ strokesJson: "{\"strokes\":[]}", png: new Uint8Array([1, 2, 3]) }); }}>signer</button>
    </section>
  ),
}));

const site = "0192f0c1-0000-7000-8000-000000005001";
const asset = "0192f0c1-0000-7000-8000-000000000a01";
const meter = "0192f0c1-0000-7000-8000-000000000b01";
const previousEntry = "0192f0c1-0000-7000-8000-0000000e0000";
const operator = { userId: "0192f0c1-0000-7000-8000-000000000501", name: "Karim", employeeId: "OP-1", checkedBy: "tablet" as const };

async function tablet(): Promise<{ db: KarryOfflineDb; device: DeviceRecord }> {
  const db = openOfflineDb(`start-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  const device: DeviceRecord = {
    id: "this", deviceId: "0192f0c1-0000-7000-8000-000000000d01", tenantId: "t1", siteId: site, privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32)), enrolledAt: "2026-11-09T05:00:00Z", clockOffsetMs: 0,
  };
  await db.device.put(device);
  await db.sites.put({ id: site, code: "NORD", name: "Carrière Nord", type: "quarry", timeZone: "Africa/Tunis" });
  await db.shiftPatterns.put({
    id: "p1", siteId: site, effectiveFrom: "2026-01-01", shifts: [
      { code: "M", name: "Matin", start: "06:00", end: "14:00", days: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] },
      { code: "A", name: "Après-midi", start: "14:00", end: "22:00", days: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] },
      { code: "N", name: "Nuit", start: "22:00", end: "06:00", days: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"] },
    ],
  });
  await db.assets.put({
    id: asset, code: "PE-01", name: "Pelle", type: "excavator", siteId: site, status: "active", make: null, model: null, serialNumber: null, plate: null, labelCode: null,
    words: ["pe", "01", "pelle"],
  });
  await db.meters.put({ id: meter, assetId: asset, kind: "hours", unit: "h", status: "ok" });
  await db.meterStates.put({ id: meter, lastEnd: { entryId: previousEntry, value: "1200.0", at: "2026-11-09T21:00:00Z" }, open: null });
  return { db, device };
}

const photo: Photo = {
  content: new Uint8Array(32).fill(5), contentType: "image/webp", thumbnail: new Uint8Array([5]),
  capture: { sha256: "a".repeat(64), capturedAt: "2026-11-10T02:01:00.000Z", deviceId: "d1", appVersion: "1.4.0", width: 1600, height: 1200 },
};

async function open(db: KarryOfflineDb, device: DeviceRecord, session = operator) {
  const camera: Camera = { start: () => Promise.resolve({ stop: () => undefined, focus: () => Promise.resolve() }) };
  const environment: FormEnvironment = {
    db, device, session, scanner: null, camera, take: () => Promise.resolve(photo), appVersion: "1.4.0",
    position: () => Promise.resolve(null), now: () => new Date("2026-11-10T02:02:00Z"),
  };
  const onDone = vi.fn();
  const onCancel = vi.fn();
  const view = render(<I18nProvider i18n={await createI18n({ language: "fr" })}><StartShift environment={environment} onDone={onDone} onCancel={onCancel} /></I18nProvider>);
  return { onDone, onCancel, unmount: view.unmount };
}

async function chooseMachine() {
  fireEvent.change(screen.getByLabelText("Code, nom ou étiquette"), { target: { value: "PE-01" } });
  fireEvent.click(await screen.findByRole("button", { name: /PE-01/ }));
  fireEvent.click(await screen.findByRole("button", { name: "Choisir cette machine" }));
  await screen.findByRole("region", { name: "Machine" });
}

async function takeThePhoto() {
  fireEvent.click(screen.getByRole("button", { name: "Photographier le compteur" }));
  fireEvent.loadedData(await screen.findByLabelText("Caméra : cadrez ce qu’il faut photographier"));
  fireEvent.click(await screen.findByRole("button", { name: "Prendre la photo" }));
  fireEvent.click(await screen.findByRole("button", { name: "Garder cette photo" }));
  await screen.findByText("Photo du compteur prise");
}

describe("starting a shift on the tablet (US-P2-FO-01, P2.5)", () => {
  it("TC-P2-FO-001: offline, the night shift across midnight is preselected, and the signed start goes to the outbox", async () => {
    const { db, device } = await tablet();
    const { onDone } = await open(db, device);

    expect(screen.getByText("Karim (OP-1)"), "who is unlocked shows from the first step (T-01)").toBeInTheDocument();
    await chooseMachine();
    expect(screen.getByText(/^Poste : Nuit/)).toBeInTheDocument();
    expect(screen.getByText(/^Fin précédente : 1200\.0 h/)).toBeInTheDocument();
    await takeThePhoto();
    fireEvent.change(screen.getByLabelText("Valeur de début (h)"), { target: { value: "1200.5" } });
    fireEvent.click(screen.getByRole("button", { name: "Vérifier et signer" }));
    fireEvent.click(await screen.findByRole("button", { name: "signer" }));

    await waitFor(() => { expect(onDone).toHaveBeenCalled(); });
    expect(await db.drafts.count(), "a signed form leaves no draft").toBe(0);
    const [command] = await db.outbox.toArray();
    expect(command?.type).toBe(startCommand);
    expect((command?.payload as { record: unknown }).record).toMatchObject({
      assetId: asset, meterId: meter, startValue: "1200.5", fuelStartPercent: 50, startedAt: "2026-11-10T02:02:00.000Z", startPhoto: `sha256:${"a".repeat(64)}`,
      // The previous segment ended when this night shift began: a handover within the shift (US-P2-FO-05 AC1).
      previousRootId: previousEntry,
    });
    await db.delete();
  });

  it("TC-P2-FO-003: a start below the previous end is blocked on the tablet with the explanation", async () => {
    const { db, device } = await tablet();
    await open(db, device);
    await chooseMachine();
    await takeThePhoto();

    fireEvent.change(screen.getByLabelText("Valeur de début (h)"), { target: { value: "1199.9" } });

    expect(screen.getByRole("alert")).toHaveTextContent("La valeur est inférieure à la fin précédente (1200.0 h).");
    expect(screen.getByRole("button", { name: "Vérifier et signer" })).toBeDisabled();
    await db.delete();
  });

  it("asks for a reason when the meter jumped more than 24 hours, and cannot be signed without a photo", async () => {
    const { db, device } = await tablet();
    await open(db, device);
    await chooseMachine();

    fireEvent.change(screen.getByLabelText("Valeur de début (h)"), { target: { value: "1300.0" } });
    expect(screen.getByRole("button", { name: "Vérifier et signer" })).toBeDisabled();
    await takeThePhoto();
    expect(screen.getByText(/Le compteur a avancé de plus de 24 h/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Vérifier et signer" })).toBeDisabled();

    fireEvent.change(screen.getByLabelText("Raison de l’écart (obligatoire)"), { target: { value: "Compteur changé hier" } });
    expect(screen.getByRole("button", { name: "Vérifier et signer" })).toBeEnabled();
    await db.delete();
  });

  it("says when another operator's shift runs on the machine and offers nothing to sign", async () => {
    const { db, device } = await tablet();
    await db.users.put({ id: "u-sami", employeeId: "OP-2", name: "Sami", pin: null });
    await db.meterStates.put({
      id: meter, lastEnd: null,
      open: { entryId: "e1", version: 1, operatorId: "u-sami", startedAt: "2026-11-09T21:00:00Z", startValue: "1200.0", shiftDate: "2026-11-09", shiftCode: "N" },
    });
    await open(db, device);
    await chooseMachine();

    expect(screen.getByRole("alert")).toHaveTextContent(/^Sami a un poste en cours sur cette machine/);
    expect(screen.queryByRole("button", { name: "Photographier le compteur" })).not.toBeInTheDocument();
    await db.delete();
  });

  it("keeps a form left by an auto-lock for the same operator, never for another one, and drops it when left on purpose (US-P2-FO-01 AC6, T-01)", async () => {
    const { db, device } = await tablet();
    const first = await open(db, device);
    await chooseMachine();
    await takeThePhoto();
    fireEvent.change(screen.getByLabelText("Valeur de début (h)"), { target: { value: "1201.0" } });
    await waitFor(async () => { expect((await db.drafts.toArray())[0]?.values).toMatchObject({ machineId: asset, value: "1201.0", fuel: 50 }); });
    first.unmount();

    const other = await open(db, device, { ...operator, userId: "0192f0c1-0000-7000-8000-000000000502", name: "Sami", employeeId: "OP-2" });
    expect(await screen.findByLabelText("Code, nom ou étiquette")).toHaveValue("");
    expect(await db.drafts.count(), "opening a form is not a draft").toBe(1);
    other.unmount();

    const again = await open(db, device);
    expect(await screen.findByRole("status")).toHaveTextContent("Vous reprenez le début de poste laissé en cours.");
    expect(screen.getByLabelText("Valeur de début (h)")).toHaveValue("1201.0");
    expect(screen.getByText("Photo du compteur prise")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Annuler le début de poste" }));
    await waitFor(() => { expect(again.onCancel).toHaveBeenCalled(); });
    expect(await db.drafts.count()).toBe(0);
    await db.delete();
  });
});

describe("the pre-start checklist on the tablet (US-P2-FO-02, P2.6)", () => {
  const checklist = (required: boolean) => ({
    id: "0192f0c1-0000-7000-8000-000000008001", assetType: "excavator", version: 3, required,
    items: [
      { code: "brakes", texts: { fr: "Freins", en: "Brakes" }, critical: true, photoOnNotOk: true },
      { code: "lights", texts: { fr: "Feux" }, critical: false, photoOnNotOk: false },
    ],
  });

  const answer = async (item: string, value: string) => {
    fireEvent.click(within(await screen.findByRole("group", { name: item })).getByRole("button", { name: value }));
  };

  it("a required checklist comes first, is signed with the version it answered, and the start follows", async () => {
    const { db, device } = await tablet();
    await db.checklistTemplates.put(checklist(true));
    const { onDone } = await open(db, device);
    await chooseMachine();

    expect(screen.queryByRole("button", { name: "Photographier le compteur" }), "the start waits for the checklist").not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Passer la check-list" }), "a required checklist cannot be skipped").not.toBeInTheDocument();
    await answer("Freins", "Conforme");
    await answer("Feux", "S.O.");
    fireEvent.click(screen.getByRole("button", { name: "Signer la check-list" }));

    await screen.findByRole("button", { name: "Photographier le compteur" });
    await takeThePhoto();
    fireEvent.change(screen.getByLabelText("Valeur de début (h)"), { target: { value: "1200.5" } });
    fireEvent.click(screen.getByRole("button", { name: "Vérifier et signer" }));
    fireEvent.click(await screen.findByRole("button", { name: "signer" }));
    await waitFor(() => { expect(onDone).toHaveBeenCalled(); });

    const [signed, started] = await db.outbox.orderBy("sequence").toArray();
    expect(signed?.type).toBe(checklistCommand);
    expect(started?.type).toBe(startCommand);
    const payload = signed?.payload as { record: unknown; envelope: { recordType: string; evidence?: unknown; handwritten?: unknown }; captures?: unknown };
    expect(payload.record).toMatchObject({
      templateId: "0192f0c1-0000-7000-8000-000000008001", templateVersion: 3, assetId: asset, submittedAt: "2026-11-10T02:02:00.000Z",
      answers: [{ item: "brakes", answer: "ok" }, { item: "lights", answer: "na" }],
    });
    expect(payload.envelope.recordType).toBe("fieldops.checklist");
    expect([payload.envelope.evidence, payload.envelope.handwritten, payload.captures], "no photo, no handwriting: the device key signs for the operator").toEqual([undefined, undefined, undefined]);
    await db.delete();
  });

  it("TC-P2-FO-004: a critical Not OK needs its photo, then holds the machine and the start is refused with the reason", async () => {
    const { db, device } = await tablet();
    await db.checklistTemplates.put(checklist(true));
    await open(db, device);
    await chooseMachine();

    await answer("Freins", "Non conforme");
    await answer("Feux", "Conforme");
    expect(screen.getByRole("button", { name: "Signer la check-list" }), "the Not OK of a critical item waits for its photo").toBeDisabled();
    fireEvent.click(screen.getByRole("button", { name: "Photographier le problème" }));
    fireEvent.loadedData(await screen.findByLabelText("Caméra : cadrez ce qu’il faut photographier"));
    fireEvent.click(await screen.findByRole("button", { name: "Prendre la photo" }));
    fireEvent.click(await screen.findByRole("button", { name: "Garder cette photo" }));
    fireEvent.click(await screen.findByRole("button", { name: "Signer la check-list" }));

    expect(await screen.findByText(/^Cet engin est immobilisé comme dangereux depuis .* \(Freins\)/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Photographier le compteur" })).not.toBeInTheDocument();
    const [signed] = await db.outbox.toArray();
    const payload = signed?.payload as { record: { answers: unknown }; envelope: { evidence: string[] }; captures: unknown[] };
    expect(payload.record.answers).toEqual([{ item: "brakes", answer: "not_ok", photo: `sha256:${"a".repeat(64)}` }, { item: "lights", answer: "ok" }]);
    expect(payload.envelope.evidence).toEqual([`sha256:${"a".repeat(64)}`]);
    expect(payload.captures).toHaveLength(1);
    expect(await db.blobs.count(), "the photo stays on the tablet until it is uploaded").toBe(1);
    await db.delete();
  });

  it("a machine held by the last pull cannot be started, even without a checklist", async () => {
    const { db, device } = await tablet();
    await db.safetyHolds.put({ id: "h1", assetId: asset, items: ["brakes"], openedAt: "2026-11-10T01:00:00Z" });
    await open(db, device);
    await chooseMachine();

    expect(await screen.findByText(/^Cet engin est immobilisé comme dangereux/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Photographier le compteur" })).not.toBeInTheDocument();
    await db.delete();
  });

  it("an optional checklist may be skipped, and nothing is signed for it", async () => {
    const { db, device } = await tablet();
    await db.checklistTemplates.put(checklist(false));
    await open(db, device);
    await chooseMachine();

    fireEvent.click(await screen.findByRole("button", { name: "Passer la check-list" }));

    expect(await screen.findByRole("button", { name: "Photographier le compteur" })).toBeInTheDocument();
    expect(await db.outbox.count()).toBe(0);
    await db.delete();
  });

  it("TC-P2-FO-010: on a failed meter no reading is asked, the start is the last known end, and the start is signed", async () => {
    const { db, device } = await tablet();
    await db.meters.put({ id: meter, assetId: asset, kind: "hours", unit: "h", status: "failed" });
    const { onDone } = await open(db, device);
    await chooseMachine();
    await takeThePhoto();

    expect(screen.getByRole("note")).toHaveTextContent("Ce compteur est en panne : aucune valeur n’est demandée. Le début est la dernière fin connue (1200.0 h)");
    expect(screen.queryByLabelText("Valeur de début (h)")).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Vérifier et signer" }));
    expect(await screen.findByRole("region", { name: "pad" })).toHaveTextContent("1200.0 h (estimé)");
    fireEvent.click(screen.getByRole("button", { name: "signer" }));

    await waitFor(() => { expect(onDone).toHaveBeenCalled(); });
    const [command] = await db.outbox.toArray();
    expect((command?.payload as { record: unknown }).record).toMatchObject({ meterId: meter, startValue: "1200.0" });
    await db.delete();
  });
});

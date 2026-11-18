import "fake-indexeddb/auto";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { createI18n, I18nProvider } from "@karry/i18n";
import { openOfflineDb, type DeviceRecord, type KarryOfflineDb } from "@karry/offline";
import type { Camera, Photo } from "./camera";
import { EndShift } from "./EndShift";
import { findActiveShift } from "./endRules";
import { endCommand } from "./meterState";
import type { FormEnvironment } from "./StartShift";

vi.mock("./SignStep", () => ({
  default: ({ onConfirm, summary }: { onConfirm: (s: { strokesJson: string; png: Uint8Array }) => Promise<void>; summary: unknown }) => (
    <section aria-label="pad">
      <div>{summary as never}</div>
      <button type="button" onClick={() => { void onConfirm({ strokesJson: "{\"strokes\":[]}", png: new Uint8Array([1, 2, 3]) }); }}>signer</button>
    </section>
  ),
}));

const operator = { userId: "0192f0c1-0000-7000-8000-000000000501", name: "Karim", employeeId: "OP-1", checkedBy: "tablet" as const };
const entry = "0192f0c1-0000-7000-8000-0000000e0001";
const photo: Photo = {
  content: new Uint8Array(32).fill(6), contentType: "image/webp", thumbnail: new Uint8Array([6]),
  capture: { sha256: "b".repeat(64), capturedAt: "2026-11-09T14:00:00.000Z", deviceId: "d1", appVersion: "1.4.0", width: 1600, height: 1200 },
};

async function tablet(): Promise<{ db: KarryOfflineDb; device: DeviceRecord }> {
  const db = openOfflineDb(`end-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  const device: DeviceRecord = {
    id: "this", deviceId: "0192f0c1-0000-7000-8000-000000000d01", tenantId: "t1", siteId: "s1", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32)), enrolledAt: "2026-11-09T05:00:00Z", clockOffsetMs: 0,
  };
  await db.device.put(device);
  await db.assets.put({
    id: "0192f0c1-0000-7000-8000-000000000a01", code: "PE-01", name: "Pelle", type: "excavator", siteId: "s1", status: "active", make: null, model: null, serialNumber: null,
    plate: null, labelCode: null, words: [],
  });
  await db.meters.put({ id: "0192f0c1-0000-7000-8000-000000000b01", assetId: "0192f0c1-0000-7000-8000-000000000a01", kind: "hours", unit: "h", status: "ok" });
  await db.meterStates.put({
    id: "0192f0c1-0000-7000-8000-000000000b01", lastEnd: null,
    open: { entryId: entry, version: 1, operatorId: operator.userId, startedAt: "2026-11-09T06:00:00Z", startValue: "100.0", shiftDate: "2026-11-09", shiftCode: "M" },
  });
  return { db, device };
}

async function open(db: KarryOfflineDb, device: DeviceRecord) {
  const active = await findActiveShift(db, operator.userId);
  if (!active) throw new Error("no active shift");
  const camera: Camera = { start: () => Promise.resolve({ stop: () => undefined, focus: () => Promise.resolve() }) };
  const environment: FormEnvironment = {
    db, device, session: operator, scanner: null, camera, take: () => Promise.resolve(photo), appVersion: "1.4.0",
    position: () => Promise.resolve(null), now: () => new Date("2026-11-09T14:00:00Z"),
  };
  const onDone = vi.fn();
  render(<I18nProvider i18n={await createI18n({ language: "fr" })}><EndShift environment={environment} active={active} onDone={onDone} onCancel={vi.fn()} /></I18nProvider>);
  return { onDone };
}

async function takeThePhoto() {
  fireEvent.click(screen.getByRole("button", { name: "Photographier le compteur" }));
  fireEvent.click(await screen.findByRole("button", { name: "Prendre la photo" }));
  fireEvent.click(await screen.findByRole("button", { name: "Garder cette photo" }));
  await screen.findByText("Photo du compteur prise");
}

describe("ending a shift on the tablet (US-P2-FO-04, P2.5)", () => {
  it("shows Δ, takes the stoppages, and signs the end as the entry's next version", async () => {
    const { db, device } = await tablet();
    const { onDone } = await open(db, device);
    await takeThePhoto();

    fireEvent.change(screen.getByLabelText("Valeur de fin (h)"), { target: { value: "107.5" } });
    expect(screen.getByText("Δ : 7.5 h")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Ajouter un arrêt" }));
    const stoppage = screen.getByRole("group", { name: "Arrêt 1" });
    expect(screen.getByRole("button", { name: "Vérifier et signer" })).toBeDisabled();
    fireEvent.click(within(stoppage).getByRole("button", { name: "Météo" }));
    fireEvent.change(within(stoppage).getByLabelText("Durée en minutes"), { target: { value: "30" } });
    fireEvent.click(screen.getByRole("button", { name: "Vérifier et signer" }));
    expect(await screen.findByRole("region", { name: "pad" })).toHaveTextContent("7.5 h");
    fireEvent.click(screen.getByRole("button", { name: "signer" }));

    await waitFor(() => { expect(onDone).toHaveBeenCalled(); });
    const [command] = await db.outbox.toArray();
    expect(command?.type).toBe(endCommand);
    expect((command?.payload as { record: unknown }).record).toEqual({
      rootId: entry, version: 2, endedAt: "2026-11-09T14:00:00.000Z", endValue: "107.5", fuelEndPercent: 50, endPhoto: `sha256:${"b".repeat(64)}`,
      stoppages: [{ reason: "weather", minutes: 30 }],
    });
    expect(await findActiveShift(db, operator.userId), "the shift is no longer active on the tablet").toBeNull();
    await db.delete();
  });

  it("refuses a meter going backwards and a Δ longer than the shift, with the reason", async () => {
    const { db, device } = await tablet();
    await open(db, device);
    await takeThePhoto();

    fireEvent.change(screen.getByLabelText("Valeur de fin (h)"), { target: { value: "99.0" } });
    expect(screen.getByRole("alert")).toHaveTextContent("Le compteur a reculé : la fin est inférieure au début (100.0 h).");
    fireEvent.change(screen.getByLabelText("Valeur de fin (h)"), { target: { value: "109.0" } });
    expect(screen.getByRole("alert")).toHaveTextContent("Le compteur a tourné plus longtemps que le poste");
    expect(screen.getByRole("button", { name: "Vérifier et signer" })).toBeDisabled();
    await db.delete();
  });
});

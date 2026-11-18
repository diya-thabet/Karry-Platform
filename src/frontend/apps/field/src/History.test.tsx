import "fake-indexeddb/auto";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createI18n, I18nProvider } from "@karry/i18n";
import { openOfflineDb, type KarryOfflineDb, type OutboxRecord } from "@karry/offline";
import { readDraft, saveDraft } from "./drafts";
import { History } from "./History";
import { endCommand, startCommand } from "./meterState";

const operator = { userId: "u1", name: "Karim", employeeId: "OP-1", checkedBy: "tablet" as const };
const now = () => new Date("2026-11-10T08:00:00Z");
let sequence = 0;

function command(type: string, record: Record<string, unknown>, overrides: Partial<OutboxRecord> = {}): OutboxRecord {
  return {
    commandId: `c${String(++sequence)}`, sequence, type, deviceTime: "2026-11-09T06:00:00Z", signature: "s", status: "confirmed", attempts: 0, code: null, flags: [],
    answeredAt: null, blobs: [], payload: { record, envelope: { signer: { userId: operator.userId } } }, ...overrides,
  };
}

async function tablet(): Promise<KarryOfflineDb> {
  const db = openOfflineDb(`history-${crypto.randomUUID()}`);
  await db.assets.put({
    id: "a1", code: "PE-01", name: "Pelle", type: "excavator", siteId: "s1", status: "active", make: null, model: null, serialNumber: null, plate: null, labelCode: null, words: [],
  });
  await db.outbox.bulkPut([
    command(startCommand, { rootId: "e1", assetId: "a1", startedAt: "2026-11-09T06:00:00Z", startValue: "100.0", fuelStartPercent: 75, startPhoto: "sha256:x" }),
    command(endCommand, { rootId: "e1", version: 2, endedAt: "2026-11-09T14:00:00Z", endValue: "107.5", endPhoto: "sha256:y" }, { flags: ["RULE-FO-01"] }),
    command(startCommand, { rootId: "e2", assetId: "a1", startedAt: "2026-11-10T06:00:00Z", startValue: "90.0", fuelStartPercent: 25, startPhoto: "sha256:z" },
      { status: "rejected", code: "sync.signature_invalid" }),
  ]);
  return db;
}

async function open(db: KarryOfflineDb) {
  const onStart = vi.fn();
  render(<I18nProvider i18n={await createI18n({ language: "fr" })}><History db={db} session={operator} now={now} onBack={vi.fn()} onStart={onStart} /></I18nProvider>);
  return { onStart };
}

describe("my history on the tablet (US-P2-FO-06)", () => {
  it("lists the operator's entries with where each stands, Δ, what the controller will check and why one was refused", async () => {
    const db = await tablet();
    await open(db);

    const items = await screen.findAllByRole("listitem");
    expect(items).toHaveLength(2);
    expect(items[0]).toHaveTextContent("PE-01");
    expect(items[0]).toHaveTextContent("Refusé");
    expect(items[1]).toHaveTextContent("Reçu");
    expect(items[1]).toHaveTextContent("Δ : 7.5");
    expect(items[1]).toHaveTextContent("À vérifier : la valeur de début diffère de la fin précédente.");
    await db.delete();
  });

  it("starts a refused start again from its values (AC2 for the tablet's refusals)", async () => {
    const db = await tablet();
    const { onStart } = await open(db);

    const [refused] = await screen.findAllByRole("listitem");
    fireEvent.click(within(refused ?? document.body).getByRole("button", { name: "Recommencer avec ces valeurs" }));

    await waitFor(() => { expect(onStart).toHaveBeenCalled(); });
    expect(await readDraft(db, operator.userId, "start", now())).toEqual({ machineId: "a1", value: "90.0", fuel: 25, remarks: "", photo: null });
    await db.delete();
  });

  it("offers to continue a start left unsigned", async () => {
    const db = openOfflineDb(`history-${crypto.randomUUID()}`);
    await saveDraft(db, operator.userId, "start", { machineId: null, value: "5.0", fuel: 50, remarks: "", photo: null }, now());
    const { onStart } = await open(db);

    fireEvent.click(await screen.findByRole("button", { name: "Reprendre" }));

    expect(onStart).toHaveBeenCalled();
    await db.delete();
  });
});

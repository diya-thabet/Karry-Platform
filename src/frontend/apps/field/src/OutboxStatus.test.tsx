import "fake-indexeddb/auto";
import { render, screen, waitFor, within } from "@testing-library/react";
import { generateDeviceKey, importPinKey, publicJwk } from "@karry/crypto";
import { createI18n, I18nProvider } from "@karry/i18n";
import { applyReceipts, enqueue, openOfflineDb } from "@karry/offline";
import { OutboxStatus } from "./OutboxStatus";
import { maxBackoffMs, nextSyncMs, uploadEveryMs } from "./uploads";
import { refreshEveryMs } from "./sync";

async function tablet() {
  const db = openOfflineDb(`outbox-status-${crypto.randomUUID()}`);
  const keys = await generateDeviceKey();
  await db.device.put({
    id: "this", deviceId: "d1", tenantId: "t1", siteId: "s1", privateKey: keys.privateKey, publicKey: await publicJwk(keys.publicKey),
    pinKey: await importPinKey(new Uint8Array(32)), enrolledAt: "2026-11-09T08:00:00Z", clockOffsetMs: 0,
  });
  return db;
}

describe("the sync centre (US-P2-OFF-02)", () => {
  it("says everything is sent, then follows the outbox: entries to send, waiting, and refused ones with the reason", async () => {
    const db = await tablet();
    render(<I18nProvider i18n={await createI18n({ language: "fr" })}><OutboxStatus db={db} /></I18nProvider>);
    await screen.findByText("Tout est envoyé");

    const [first, second, third] = [
      await enqueue(db, { type: "t.v1", payload: {} }, () => Date.parse("2026-11-09T08:00:00Z")),
      await enqueue(db, { type: "t.v1", payload: {} }),
      await enqueue(db, { type: "t.v1", payload: {} }),
    ];
    await screen.findByText("3 saisies en attente d’envoi");

    await applyReceipts(db, [
      { commandId: first.commandId, status: "rejected", code: "sync.blob_mismatch", retryable: false, flags: [] },
      { commandId: second.commandId, status: "waiting", code: null, retryable: false, flags: [] },
      { commandId: third.commandId, status: "rejected", code: "test.payload_invalid", retryable: false, flags: [] },
    ], new Date("2026-11-09T09:00:00Z"));

    const section = await screen.findByRole("region", { name: "Envoi des saisies" });
    await waitFor(() => { expect(within(section).getByText("Tout est envoyé")).toBeTruthy(); });
    expect(within(section).getByText("1 saisie reçue, en attente d’une saisie précédente")).toBeTruthy();
    expect(within(section).getByText("2 saisies refusées : voir pourquoi")).toBeTruthy();
    expect(within(section).getByText("La photo reçue n’est pas celle qui a été prise. Signalez-le à un responsable.")).toBeTruthy();
    expect(within(section).getByText("Refusée par le serveur (test.payload_invalid).")).toBeTruthy();
  });
});

describe("when the tablet exchanges with the server next", () => {
  it("tries every 2 minutes while entries wait to be sent", () => {
    expect(nextSyncMs("ok", 3, 0, () => 0)).toBe(uploadEveryMs - 15_000);
    expect(nextSyncMs("ok", 3, 0, () => 1)).toBe(uploadEveryMs + 15_000);
    expect(nextSyncMs("ok", 0, 0, () => 0.5)).toBe(refreshEveryMs);
  });

  it("waits longer after each failure in a row, up to 30 minutes, with jitter", () => {
    expect(nextSyncMs("failed", 1, 1, () => 0.5)).toBe(refreshEveryMs);
    expect(nextSyncMs("failed", 1, 2, () => 0.5)).toBe(2 * refreshEveryMs);
    expect(nextSyncMs("failed", 1, 3, () => 0.5)).toBe(4 * refreshEveryMs);
    expect(nextSyncMs("failed", 1, 10, () => 0.5)).toBe(maxBackoffMs);
    expect(nextSyncMs("failed", 1, 10, () => 0)).toBe(maxBackoffMs * 0.9);
    expect(nextSyncMs("busy", 1, 0, () => 0)).toBe(60_000);
  });
});

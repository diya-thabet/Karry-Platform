import { expect, test } from "./guard";
import { enrolledTablet, enrollmentCode, meterNow, startAndEnd, unlock } from "./fieldTablet";

// Nightly only (tools/e2e/run.sh with E2E_NIGHTLY=1): about 15 minutes of work on the tablet before the upload.
const shifts = Number(process.env.KARRY_OFFLINE_SHIFTS ?? 200); // 400 signed commands, each with a photo
const hour = 3_600_000;

test.describe("three days offline (P2.12a, TC-P2-SYNC-006, ADR-0031, ADR-0032) @nightly", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-SYNC-006: 72 h offline, 200 shifts with their photos queued, then a 3G network: all sent in under 15 min, nothing lost or refused", async ({ browser, page }) => {
    test.setTimeout(3_600_000);
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    const tablet = await enrolledTablet(context, await enrollmentCode(page));
    const now = Date.now();

    // Offline from three days ago: the tablet's clock runs from then, a little more than the shift count spread over
    // the 72 h between shifts, so each entry is signed at its own time, as on a tablet that saw no network for three days.
    await context.setOffline(true);
    await tablet.clock.install({ time: now - 72 * hour });
    await tablet.clock.resume();
    const value = meterNow();
    const step = Math.floor((72 * hour - 30 * 60_000) / shifts);
    for (let shift = 0; shift < shifts; shift++) {
      await startAndEnd(tablet, value);
      // Hours pass between shifts: the tablet locks itself after its idle time, and the next operator unlocks it.
      await tablet.clock.fastForward(step);
      await expect(tablet.getByRole("heading", { name: "Déverrouiller la tablette" })).toBeVisible();
      await unlock(tablet);
    }

    const outbox = tablet.getByRole("region", { name: "Envoi des saisies" });
    await expect(outbox).toContainText(`${String(shifts * 2)} saisies en attente d’envoi`);

    // Back to today's time (the last jump locked the tablet: unlocked above), then back online on a 3G network (Chromium's throttling: 750 kb/s up, 300 ms latency).
    await tablet.clock.setSystemTime(Date.now());
    const network = await context.newCDPSession(tablet);
    await network.send("Network.enable");
    await network.send("Network.emulateNetworkConditions", {
      offline: false, latency: 300, downloadThroughput: (1.6 * 1024 * 1024) / 8, uploadThroughput: (750 * 1024) / 8,
    });
    await context.setOffline(false);
    const online = Date.now();
    await tablet.getByRole("button", { name: "Actualiser" }).click();
    await expect(outbox.getByText("Tout est envoyé")).toBeVisible({ timeout: 15 * 60_000 });
    const took = Date.now() - online;
    console.log(`TC-P2-SYNC-006: ${String(shifts * 2)} commands with their photos sent in ${(took / 60_000).toFixed(1)} min on 3G`);
    expect(took).toBeLessThan(15 * 60_000);
    await expect(tablet.getByText(/refusée/)).toHaveCount(0);

    // Every shift arrived once: the history holds them all as received, none refused.
    await tablet.getByRole("button", { name: "Mon historique" }).click();
    await expect(tablet.getByRole("listitem").filter({ hasText: "Reçu" })).toHaveCount(shifts, { timeout: 60_000 });
    await expect(tablet.getByRole("listitem").filter({ hasText: "Refusé" })).toHaveCount(0);
    await context.close();
  });
});

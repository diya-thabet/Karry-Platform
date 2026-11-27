import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "./guard";
import { enrolledTablet, enrollmentCode, meterNow, startAndEnd, unlock } from "./fieldTablet";

// The built field app served by `vite preview` (tools/e2e/run.sh): changing its service worker script is what a new
// release looks like to a tablet.
const serviceWorker = join(dirname(fileURLToPath(import.meta.url)), "..", "..", "apps", "field", "dist", "sw.js");

test.describe("a new version while entries wait (P2.5, US-P1-OFF-01 AC2, ADR-0031)", () => {
  test.use({ permissions: ["camera"] });

  test("TC-P2-SYNC-007: the update is offered on the home screen, applied with the outbox full, and every entry is sent afterwards", async ({ browser, page }) => {
    test.setTimeout(300_000);
    const released = readFileSync(serviceWorker, "utf8");
    const context = await browser.newContext({ locale: "fr-FR", permissions: ["camera"] });
    try {
      const tablet = await enrolledTablet(context, await enrollmentCode(page));
      await context.setOffline(true);
      await startAndEnd(tablet, meterNow());

      // Back online, but the uploads fail for now: the outbox stays full while the new version arrives.
      await context.route("**/api/v1/sync/batches", (route) => route.abort());
      await context.setOffline(false);
      const outbox = tablet.getByRole("region", { name: "Envoi des saisies" });
      await expect(outbox).toContainText("2 saisies en attente d’envoi");

      writeFileSync(serviceWorker, `${released}\n// karry e2e: a new release\n`);
      await tablet.evaluate(async () => { await (await navigator.serviceWorker.getRegistration())?.update(); });
      const offer = tablet.getByRole("region", { name: "Mise à jour" });
      await expect(offer).toContainText("Une nouvelle version de Karry Terrain est prête.", { timeout: 60_000 });
      // The update is applied offline, as on a site without network: the new version is already installed, so the
      // reload comes from the tablet's own cache, and nothing can leave the outbox meanwhile. No request routing during
      // the reload: with `context.route` active, the reload's navigation never committed on CI (#281, twice).
      await context.setOffline(true);
      await context.unroute("**/api/v1/sync/batches");
      await offer.getByRole("button", { name: "Mettre à jour" }).click();

      // The new version reloads the app (locked again); the signed entries are still in the outbox. Wait for the lock
      // screen of the reloaded page first: an action begun on the old page can hang across the reload (CI, #253).
      await expect(tablet.getByRole("heading", { name: "Déverrouiller la tablette" })).toBeVisible({ timeout: 30_000 });
      await unlock(tablet);
      await expect(tablet.getByRole("region", { name: "Envoi des saisies" })).toContainText("2 saisies en attente d’envoi");
      await context.setOffline(false);
      await tablet.getByRole("button", { name: "Actualiser" }).click();
      await expect(tablet.getByRole("region", { name: "Envoi des saisies" }).getByText("Tout est envoyé")).toBeVisible({ timeout: 180_000 });
      await tablet.getByRole("button", { name: "Mon historique" }).click();
      await expect(tablet.getByRole("listitem")).toHaveCount(1);
      await expect(tablet.getByRole("listitem").first()).toContainText("Reçu");
    } finally {
      writeFileSync(serviceWorker, released);
      await context.close();
    }
  });
});
